using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Process;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// "Your own pingctl" (a path used instead of the bundled binary, with Detect) and the redacted log of the
    /// pipeline, both folded; and the pipeline's events bound to the rows. The bundled pingctl needs nothing set:
    /// it is checked against its pinned SHA-256 before every push (<see cref="PingctlLocator"/>).
    /// </summary>
    public sealed partial class ShipSection
    {
        private const int MaxLogLines = 500;

        private TextField pingctlField;
        private Label pingctlStatus;
        private ScrollView logView;
        private SectionActions toolActions;
        private PingctlLocation located;
        private DateTime locatedAtUtc;

        /// <summary>Where pingctl was found, or why none may run; re-checked at most every ten seconds for the section chip (a push always checks again).</summary>
        public PingctlLocation Pingctl
        {
            get
            {
                if (located == null || DateTime.UtcNow - locatedAtUtc > TimeSpan.FromSeconds(10))
                {
                    located = PingctlLocator.LocateHere(WorkspaceContext.LoadUser(out _).PingctlPath);
                    locatedAtUtc = DateTime.UtcNow;
                }

                return located;
            }
        }

        private void BuildTools(VisualElement body)
        {
            toolActions = new SectionActions();
            var own = new Foldout { text = "Your own pingctl", value = false };
            own.Add(Ui.Note("Push uses the pingctl the package carries, checked against its pinned SHA-256 before every use. Set a path here only to use your own copy instead; leave it empty for the bundled one."));
            pingctlField = Ui.Text("pingctl path", WorkspaceContext.LoadUser(out _).PingctlPath);
            pingctlField.RegisterCallback<FocusOutEvent>(_ => SectionActions.Guard(pingctlStatus, () => SavePingctlPath(pingctlField.value), Changed));
            own.Add(Ui.Row(pingctlField, Ui.Button("Detect", () => toolActions.Run(pingctlStatus, DetectPingctlAsync, Changed))));
            pingctlStatus = Ui.Status();
            own.Add(pingctlStatus);
            body.Add(own);

            var log = new Foldout { text = "Log", value = false };
            logView = Ui.WithClass(new ScrollView(), "pingcore-log");
            log.Add(logView);
            body.Add(log);
        }

        private void SavePingctlPath(string path)
        {
            Settings.EditorUserSettings user = Settings.EditorUserSettings.Load(WorkspaceContext.ProjectRoot);
            user.PingctlPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            user.Save(WorkspaceContext.ProjectRoot);
            located = null;
        }

        private async Task DetectPingctlAsync(CancellationToken ct)
        {
            SavePingctlPath(pingctlField.value);
            PingctlLocation found = Pingctl;
            if (!found.Found)
            {
                pingctlStatus.text = found.Problem + " " + PingctlLocator.GetPingctlHint;
                return;
            }

            pingctlStatus.text = $"Found {found.Path} ({found.Source}); asking its version...";
            ProcessResult result;
            // The same checked, held run copy a push uses, so Detect runs exactly what Push would run.
            using (PingctlRunCopy run = PingctlRunCopy.Prepare(WorkspaceContext.ProjectRoot, found))
            {
                if (!run.Ok)
                {
                    pingctlStatus.text = run.Problem + " " + PingctlLocator.GetPingctlHint;
                    return;
                }

                var runner = new ChildProcessRunner();
                if (run.Bundled && System.IO.Path.DirectorySeparatorChar != '\\')
                {
                    await runner.RunAsync(new ProcessSpec { FileName = "/bin/chmod", Args = new[] { "u+x", run.Path }, Timeout = TimeSpan.FromSeconds(10), Step = "pingctl-version" }, ct);
                }

                result = await runner.RunAsync(new ProcessSpec
                {
                    FileName = run.Path,
                    Args = PingctlCommand.VersionArguments(),
                    Timeout = TimeSpan.FromSeconds(20),
                    Step = "pingctl-version",
                }, ct);
            }
            string version = result.Lines.Select(l => l.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            pingctlStatus.text = result.Ok
                ? $"pingctl from {found.Source}: {version ?? "no version printed"}."
                : $"Found {found.Path} ({found.Source}), but it did not run: {(result.Error != null ? ErrorText.Of(result.Error) : "exit code " + result.ExitCode)}";
        }

        private void BindEvents()
        {
            if (commands?.Events == null)
            {
                return;
            }

            commands.Events.LogLine += OnLog;
            commands.Events.ReleaseProgressChanged += OnRelease;
            commands.Events.RunStatusChanged += OnRunStatus;
        }

        private void UnbindEvents()
        {
            if (commands?.Events == null)
            {
                return;
            }

            commands.Events.LogLine -= OnLog;
            commands.Events.ReleaseProgressChanged -= OnRelease;
            commands.Events.RunStatusChanged -= OnRunStatus;
        }

        // Pipeline lines are redacted by the runner; the view only keeps the newest ones.
        private void OnLog(string line)
        {
            if (logView == null || string.IsNullOrEmpty(line))
            {
                return;
            }

            logView.Add(new Label(line));
            while (logView.contentContainer.childCount > MaxLogLines)
            {
                logView.contentContainer.RemoveAt(0);
            }

            logView.scrollOffset = new Vector2(0, float.MaxValue);
            Running(line);
        }

        private void OnRelease(ReleaseProgressView progress) => RefreshRelease();

        private void OnRunStatus(PipelineRunStatus status) => RefreshRelease();

        // The running row shows the pipeline's latest line.
        private void Running(string line)
        {
            foreach (ShipRowView row in new[] { buildRow, pushRow, releaseRow })
            {
                row?.Running(line);
            }
        }
    }
}
