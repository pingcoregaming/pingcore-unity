using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// "Push to branch": the game's branches as Connect read them (<c>GET my-games/{id}</c>, key-only), the one Push
    /// pushes to, and what that branch says (<see cref="PushTargetResolution"/>): "Pushes to CDN source #31", or why it
    /// cannot, with Open branch in panel on the branch's edit page. The game's only branch is taken without asking;
    /// with several the developer picks, and the choice (a branch id) is kept in
    /// <c>ProjectSettings/PingCoreEditor.json</c>.
    /// </summary>
    public sealed partial class ShipSection
    {
        private const string PickChoice = "(pick the branch to push to)";
        private const string NoBranchChoice = "(no branch)";

        private DropdownField branchField;
        private Label targetLine;
        private Button openBranch;

        private void BuildBranchArea(VisualElement body)
        {
            branchField = new DropdownField("Push to branch", new List<string>(), 0);
            branchField.AddToClassList("pingcore-grow");
            branchField.RegisterValueChangedCallback(e => SectionActions.Guard(targetLine, () => SaveBranch(e.newValue), Changed));
            openBranch = Ui.Button("Open branch in panel", OpenBranch);
            body.Add(Ui.Row(branchField, openBranch));
            targetLine = Ui.Status();
            body.Add(targetLine);
        }

        // The branch Push pushes to, from the fleet Connect read and the remembered branch.
        private PushTarget Target() => PushTargetResolution.ForFleet(Facts(), WorkspaceContext.LoadProject(out _).GameBranchId);

        private void RefreshBranch()
        {
            if (branchField == null)
            {
                return;
            }

            PushTarget target = Target();
            List<string> choices = PushTargetResolution.Choices(target.Branches).ToList();
            int picked = target.Branch == null ? -1 : target.Branches.ToList().FindIndex(b => b != null && b.GameBranchId == target.Branch.GameBranchId);
            branchField.choices = choices.Count == 0 ? new List<string> { NoBranchChoice } : choices;
            branchField.SetValueWithoutNotify(picked >= 0 ? choices[picked] : choices.Count == 0 ? NoBranchChoice : PickChoice);
            targetLine.text = target.Line + (target.Warning == null ? string.Empty : "\n" + target.Warning);
            openBranch.style.display = target.GameId > 0 && target.Kind != PushTargetKind.NotRead ? DisplayStyle.Flex : DisplayStyle.None;
            openBranch.text = target.Branch != null ? "Open branch in panel" : "Open branches in panel";
        }

        private void SaveBranch(string choice)
        {
            IReadOnlyList<GameBranchView> branches = Target().Branches;
            int index = PushTargetResolution.Choices(branches).ToList().IndexOf(choice);
            GameBranchView branch = index >= 0 ? branches[index] : null;
            if (branch == null)
            {
                return;
            }

            EditorProjectSettings project = EditorProjectSettings.Load(WorkspaceContext.ProjectRoot);
            if (project.GameBranchId != branch.GameBranchId)
            {
                project.GameBranchId = branch.GameBranchId;
                project.Save(WorkspaceContext.ProjectRoot);
            }
        }

        // The branch's edit page (its data source and CDN source), or the game's branches when none is picked.
        private void OpenBranch()
        {
            PushTarget target = Target();
            if (target.GameId <= 0)
            {
                return;
            }

            Application.OpenURL(target.Branch != null
                ? PanelLinks.Branch(connectSection?.PanelBase, target.GameId, target.Branch.GameBranchId)
                : PanelLinks.GameBranches(connectSection?.PanelBase, target.GameId));
        }

        // Push's Open in panel: the branch it pushes to, else the fleet.
        private void OpenPushInPanel()
        {
            if (Target().GameId > 0)
            {
                OpenBranch();
                return;
            }

            OpenFleet();
        }
    }
}
