using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// Server executable: the file the Build row writes, from the same startup command read Push checks against (the
    /// fleet's deployments' template sets, else the game's; never a branch's default deployment spec), decided by the pure
    /// <see cref="BuildExecutableChoice"/>. When the game launches several files, a picker beside the build profile lists
    /// them and the pick is kept as <see cref="EditorProjectSettings.ProcessName"/>; it shows only after a read found
    /// several (Build reads every time it is pressed).
    /// </summary>
    public sealed partial class ShipSection
    {
        private const string PickExecutableChoice = "(pick the file this build writes)";

        private VisualElement executableRow;
        private DropdownField executableField;
        private BuildExecutableChoice lastExecutable;
        private long lastExecutableGameId;

        private void BuildExecutableArea(VisualElement body)
        {
            executableField = new DropdownField("Server executable", new List<string>(), 0);
            executableField.AddToClassList("pingcore-grow");
            executableField.tooltip = "Your game's startup commands launch different files; the build is named after the one picked here.";
            executableField.RegisterValueChangedCallback(e => SectionActions.Guard(buildRowStatus, () => SaveProcessName(e.newValue), Changed));
            executableRow = Ui.Row(executableField);
            body.Add(executableRow);
            RefreshExecutable();
        }

        // Shown only while the last read found several files; the current pick, else a prompt.
        private void RefreshExecutable()
        {
            if (executableRow == null)
            {
                return;
            }

            // A read of another game (Connect picked a fleet of another game since) offers nothing.
            if (lastExecutable != null && lastExecutableGameId != WorkspaceContext.LoadProject(out _).GameId)
            {
                lastExecutable = null;
            }

            IReadOnlyList<StartupCandidate> choices = lastExecutable?.Choices ?? new List<StartupCandidate>();
            executableRow.style.display = choices.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
            if (choices.Count <= 1)
            {
                return;
            }

            List<string> labels = choices.Select(c => c.Label()).ToList();
            string pickedPath = BuildExecutableChoice.PickedPath(WorkspaceContext.LoadProject(out _).ProcessName);
            int index = choices.ToList().FindIndex(c => c.Executable.RelativePath == pickedPath);
            executableField.choices = labels;
            executableField.SetValueWithoutNotify(index >= 0 ? labels[index] : PickExecutableChoice);
        }

        private void SaveProcessName(string label)
        {
            StartupCandidate picked = (lastExecutable?.Choices ?? new List<StartupCandidate>()).FirstOrDefault(c => c.Label() == label);
            if (picked == null)
            {
                return;
            }

            EditorProjectSettings project = EditorProjectSettings.Load(WorkspaceContext.ProjectRoot);
            project.ProcessName = BuildExecutableChoice.ProcessNameFor(picked.Executable);
            project.Save(WorkspaceContext.ProjectRoot);
            buildRowStatus.text = $"The build writes {picked.Executable.RelativePath} ({picked.Executable.Written}). Press Build.";
        }

        // The file the build writes, read now: the startup command of the fleet's deployments, else of the game's template
        // sets; the pick when there are several; else the project's product name, said plainly.
        private async Task<BuildExecutableChoice> BuildExecutableAsync(CancellationToken ct)
        {
            PushTarget target = Target();
            IPingCoreApi api = WorkspaceContext.SignedInApi(out _);
            StartupCheck check = api == null
                ? StartupCheck.Skip("sign in under Connect to read it.")
                : target.GameId <= 0
                    ? StartupCheck.Skip("pick a fleet with a game under Connect to read it.")
                    : await StartupCommandReader.ReadAsync(api, target, ct);
            lastExecutable = BuildExecutableChoice.Decide(check, WorkspaceContext.LoadProject(out _).ProcessName, PlayerSettings.productName);
            lastExecutableGameId = target.GameId;
            buildRowStatus.text = lastExecutable.Line;
            RefreshExecutable();
            return lastExecutable;
        }
    }
}
