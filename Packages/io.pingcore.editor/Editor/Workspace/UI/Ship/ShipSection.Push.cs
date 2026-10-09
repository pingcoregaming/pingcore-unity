using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Connect;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// The Push row: the folder it sends (the last build, or one the developer chose) with its file count and size,
    /// and the push itself. Push uploads to the CDN source of the fleet's game branch, never one taken from its
    /// deployments: before the pipeline runs it reads the fleet and its game again, takes the CDN source of the branch it
    /// pushes to (<c>ShipSection.Branch.cs</c>), reads the game's startup command (<see cref="StartupCommandReader"/>:
    /// the fleet's deployments' template sets, every file required; with no deployment the game's template sets, any one
    /// file; skipped with a reason when none names a process; never a branch's default deployment spec), and, only when
    /// the credential store holds no push token for that source, issues one after the developer confirmed that any other
    /// copy stops working (<c>ShipSection.Token.cs</c>).
    /// </summary>
    public sealed partial class ShipSection
    {
        private Label folderLine;
        private string summaryFor;

        private void BuildPushArea(VisualElement body)
        {
            BuildBranchArea(body);
            folderLine = Ui.Status();
            body.Add(Ui.Row(Ui.WithClass(folderLine, "pingcore-grow"), Ui.Button("Choose folder...", ChooseFolder)));
            BuildTokenArea(body);
        }

        private void RefreshPush()
        {
            if (folderLine == null)
            {
                return;
            }

            RefreshBranch();
            string folder = WorkspaceContext.LoadUser(out _).PushFolder;
            if (string.IsNullOrEmpty(folder))
            {
                folderLine.text = "Push sends the last build, or a folder you choose: none yet.";
                summaryFor = null;
            }
            else if (summaryFor != folder)
            {
                summaryFor = folder;
                string full = Path.GetFullPath(Path.Combine(WorkspaceContext.ProjectRoot, folder));
                folderLine.text = "Push sends " + (Directory.Exists(full) ? PushFolderSummary.Read(full, null).Describe() : folder + ": the folder does not exist.");
            }

            RefreshToken();
        }

        private void ChooseFolder()
        {
            string current = WorkspaceContext.LoadUser(out _).PushFolder;
            string start = string.IsNullOrEmpty(current) ? Path.Combine(WorkspaceContext.ProjectRoot, "Builds") : Path.Combine(WorkspaceContext.ProjectRoot, current);
            string picked = EditorUtility.OpenFolderPanel("Choose the server build folder to push", Directory.Exists(start) ? start : WorkspaceContext.ProjectRoot, string.Empty);
            if (!string.IsNullOrEmpty(picked))
            {
                SetPushFolder(picked);
                Changed();
            }
        }

        private async Task PushAsync(ShipRowView row, CancellationToken ct)
        {
            IPingCoreApi api = SignedInApi(row);
            (FleetFacts facts, PushTarget target, StartupCheck check) = api == null ? (null, null, null) : await PushFactsAsync(api, row, ct);
            if (facts == null)
            {
                return;
            }

            string folder = WorkspaceContext.LoadUser(out _).PushFolder;
            string full = string.IsNullOrEmpty(folder) ? null : Path.GetFullPath(Path.Combine(WorkspaceContext.ProjectRoot, folder));
            StartupFiles startup = check.Files;
            string exe = startup?.Paths.FirstOrDefault();
            if (full != null)
            {
                string required = check.Candidates.Count > 1 ? " " + check.Describe() : string.Empty;
                row.Running("Pushing " + PushFolderSummary.Read(full, exe == null ? null : Path.GetFileNameWithoutExtension(exe)).Describe() + required);
            }

            var request = new DeployRequest
            {
                Push = true,
                BuildVersion = DeployVersion.IsValid(versionField.value?.Trim()) ? versionField.value.Trim() : DefaultVersion(),
                FleetId = facts.FleetId,
                CdnSourceId = target.CdnSourceId,
                PushFolder = folder,
                Startup = startup,
                StartupCheckSkipped = check.Skipped,
            };

            // Everything that would refuse the push is checked before a token is issued: a refused push never
            // replaces another holder's token.
            string refusal = commands.CheckPush(request);
            if (refusal != null)
            {
                row.Raise(ShipRowEvent.Fail, refusal);
                return;
            }

            if (!await EnsurePushTokenAsync(api, target.CdnSourceId, false, row, ct))
            {
                return;
            }

            if (await RunAsync(row, request, false, ct, StartupNote(check, full)))
            {
                RememberSnapshot(facts.FleetId, commands.State?.Snapshot, target.CdnSourceId);
            }
        }

        // What the Push row adds to its result: why the startup check was skipped, or what it left out and, for a fleet
        // with no deployment and several template sets, which file the build holds. A refusal already names the files.
        private static string StartupNote(StartupCheck check, string folder)
        {
            if (check.Skipped != null)
            {
                return check.Skipped;
            }

            var parts = new List<string>();
            if (check.AnyOf && check.Candidates.Count > 1 && folder != null)
            {
                string matched = null;
                try
                {
                    if (Directory.Exists(folder))
                    {
                        IEnumerable<string> files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                            .Select(f => f.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        matched = check.MatchedLine(files);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // The folder check names an unreadable folder in its own words; the note just leaves the match out.
                }

                if (matched != null)
                {
                    parts.Add(matched);
                }
            }

            if (check.Note != null)
            {
                parts.Add(check.Note);
            }

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        // The fleet and its game as they are now (a branch's data source can change in the panel), the branch's CDN
        // source and the files the game's startup command launches (the fleet's deployments' template sets, else the
        // game's). The CDN source never comes from the deployments: a fleet with none still pushes.
        private async Task<(FleetFacts Facts, PushTarget Target, StartupCheck Check)> PushFactsAsync(IPingCoreApi api, ShipRowView row, CancellationToken ct)
        {
            Settings.EditorProjectSettings project = WorkspaceContext.LoadProject(out _);
            (FleetFacts facts, PluginError error) = await ConnectState.Read(api, project.FleetId, ct);
            if (facts == null)
            {
                row.Raise(ShipRowEvent.Fail, ErrorText.Of(error));
                return (null, null, null);
            }

            connect.Use(facts);
            PushTarget target = PushTargetResolution.ForFleet(facts, project.GameBranchId);
            if (!target.CanPush)
            {
                row.Raise(ShipRowEvent.Fail, target.Line);
                return (null, null, null);
            }

            StartupCheck check = await StartupCommandReader.ReadAsync(api, target, ct);
            if (check.Problem != null)
            {
                row.Raise(ShipRowEvent.Fail, check.Problem);
                return (null, null, null);
            }

            return (facts, target, check);
        }
    }
}
