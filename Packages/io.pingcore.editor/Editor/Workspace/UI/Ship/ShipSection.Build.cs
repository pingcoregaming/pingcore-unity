using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Build;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>The three rows, the Build row, and the one way every row runs the pipeline.</summary>
    public sealed partial class ShipSection
    {
        private ShipRowView buildRow;
        private ShipRowView pushRow;
        private ShipRowView releaseRow;
        private Label buildRowStatus;
        private Label shipStatus;

        private void BuildRows(VisualElement body)
        {
            shipStatus = Ui.Status();
            body.Add(shipStatus);
            body.Add(Ui.Section("Build"));
            buildRow = new ShipRowView(ShipRowKind.Build, "Build", () => StartRow(buildRow, BuildAsync), OpenFleet);
            body.Add(buildRow.Root);
            buildRowStatus = Ui.Status();
            body.Add(buildRowStatus);

            body.Add(Ui.Section("Push"));
            BuildPushArea(body);
            pushRow = new ShipRowView(ShipRowKind.Push, "Push", () => StartRow(pushRow, PushAsync), OpenPushInPanel);
            body.Add(pushRow.Root);

            body.Add(Ui.Section("Release"));
            BuildReleaseArea(body);
            releaseRow = new ShipRowView(ShipRowKind.Release, "Release", () => StartRow(releaseRow, ReleaseAsync), OpenFleet);
            body.Add(releaseRow.Root);
            BuildReleaseProgress(body);
        }

        private void RestoreRows()
        {
            bool running = commands?.Events?.RunStatus == PipelineRunStatus.Running;
            buildRow.Restore(running);
            pushRow.Restore(running);
            releaseRow.Restore(running);
        }

        // A row's button and Retry: one row at a time, from the press to its end (the reads, a token dialog and the
        // pipeline alike), and refused while that row runs (the state machine says so); else run.
        private async void StartRow(ShipRowView row, Func<ShipRowView, CancellationToken, Task> work)
        {
            if (busyRow != null || commands?.Events?.RunStatus == PipelineRunStatus.Running || tokenActions?.Busy == true)
            {
                if (busyRow != row)
                {
                    string running = busyRow != null ? busyRow.Kind.ToString() : tokenActions?.Busy == true ? "the push token check" : "another row";
                    shipStatus.text = $"{row.Kind} waits: {running} is running. Wait for it, or press Stop under Release.";
                }

                return;
            }

            EnsureCommands();
            if (!row.Raise(ShipRowEvent.Start, "Starting..."))
            {
                return;
            }

            row.Started(() => StartRow(row, work));

            shipStatus.text = string.Empty;
            busyRow = row;
            var cancellation = new CancellationTokenSource();
            runCancellation = cancellation;
            try
            {
                await work(row, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                row.Raise(ShipRowEvent.Fail, "Stopped.");
            }
            catch (Exception e)
            {
                row.Raise(ShipRowEvent.Fail, "Stopped on an unexpected " + e.GetType().Name + "; see the log.");
            }
            finally
            {
                if (row.Row.State == ShipRowState.Running)
                {
                    row.Raise(ShipRowEvent.Fail, "Stopped without an answer; press Retry.");
                }

                if (runCancellation == cancellation)
                {
                    runCancellation = null;
                }

                cancellation.Dispose();
                busyRow = null;
                Changed();
                if (cancelReleaseWhenStopped)
                {
                    cancelReleaseWhenStopped = false;
                    StartRow(releaseRow, CancelReleaseAsync);
                }
            }
        }

        // Runs one pipeline request and folds its outcome into the row, with <note> after its sentence; true on success.
        private async Task<bool> RunAsync(ShipRowView row, DeployRequest request, bool resume, CancellationToken ct, string note = null)
        {
            string problem = resume ? null : request.Problem();
            if (problem != null)
            {
                row.Raise(ShipRowEvent.Fail, problem);
                return false;
            }

            PipelineOutcome outcome = resume ? await commands.ContinueAsync(request, ct) : await commands.RunAsync(request, ct);
            ShipRowEvent e = ShipRowMachine.EventOf(outcome);
            row.Raise(e, (outcome?.Message ?? "No answer.") + (string.IsNullOrEmpty(note) ? string.Empty : " " + note));
            return e == ShipRowEvent.Succeed;
        }

        private async Task BuildAsync(ShipRowView row, CancellationToken ct)
        {
            EditorProjectSettings project = WorkspaceContext.LoadProject(out string problem);
            if (problem != null)
            {
                row.Raise(ShipRowEvent.Fail, problem);
                return;
            }

            BuildExecutableChoice executable = await BuildExecutableAsync(ct);
            if (executable.NeedsPick)
            {
                row.Raise(ShipRowEvent.Fail, executable.Line);
                return;
            }

            string version = versionField.value?.Trim();
            var request = new DeployRequest
            {
                Build = true,
                BuildVersion = version,
                FleetId = project.FleetId,
                BuildProfile = project.BuildProfile,
                BuildExecutable = executable.Executable,
            };

            if (await RunAsync(row, request, false, ct))
            {
                string folder = DeployPlanner.BuildFolderFor(version);
                SetPushFolder(folder);
            }
        }

        // The folder Push sends next, in the developer's own settings.
        private void SetPushFolder(string folder)
        {
            Sections.SectionActions.Guard(buildRowStatus, () =>
            {
                Settings.EditorUserSettings user = Settings.EditorUserSettings.Load(WorkspaceContext.ProjectRoot);
                user.PushFolder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
                user.Save(WorkspaceContext.ProjectRoot);
            });
            summaryFor = null;
        }
    }
}
