using System.Collections.Generic;
using PingCore.Discovery.Client;
using PingCore.Editor.Build;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.PlayerHosting;
using PingCore.Editor.Workspace.UI.Sections;
using PingCore.Editor.Workspace.UI.Ship;
using PingCore.Editor.Workspace.UI.Status;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI
{
    /// <summary>
    /// The plugin's one window, Window > PingCore: a single scrolling page of four sections in order
    /// (<see cref="SectionBook"/>): Connect (sign in, pick the fleet), Ship (Build, Push, Release), Status (the
    /// fleet's live state, read only) and the Player hosting fold (off by default). Each shows its header and status
    /// chip, one line saying what it is, where it stands with its one next action, and its body
    /// (<see cref="SectionView"/>). The window gathers the facts the section model needs after every action, on
    /// focus, when it is built (a domain reload, an Editor start) and once more when the Editor has finished loading; the
    /// picked fleet is read when the window is built and again on a focus once its facts are a minute old. It serializes nothing: the project's choices live in the settings files,
    /// a row's last state in <c>SessionState</c>, and a key or token never in the window.
    /// </summary>
    public sealed class PingCoreWindow : EditorWindow
    {
        private readonly Dictionary<Section, SectionView> views = new Dictionary<Section, SectionView>();
        private ConnectState connectState;
        private ConnectSection connect;
        private ShipSection ship;
        private StatusSection status;
        private PlayerHostingFold playerHosting;
        private SignInModel model;
        private VisualElement serverTargetBox;
        private Label serverTargetBanner;
        private Label problemBanner;
        private Label overrideBanner;

        [MenuItem(PingCoreMenu.WindowItem, priority = 1)]
        public static void Open()
        {
            PingCoreWindow window = GetWindow<PingCoreWindow>(false, "PingCore", true);
            window.titleContent = new GUIContent("PingCore");
            window.Show();
        }

        private void OnEnable()
        {
            connectState = new ConnectState();
            connect = new ConnectSection();
            ship = new ShipSection();
            status = new StatusSection();
            playerHosting = new PlayerHostingFold();
            ship.Enable();
        }

        private void OnDisable()
        {
            ship?.Disable();
            connect?.Disable();
        }

        private void OnFocus()
        {
            // Facts older than a minute are read again, so a deployment added in the panel reaches the chips without a press.
            if (views.Count > 0)
            {
                connect.ReadAgainIfOld();
                status.ReadAgainIfOld();
            }

            RefreshPage();
        }

        /// <summary>Builds the page: the banners and the four sections.</summary>
        public void CreateGUI()
        {
            VisualElement root = Ui.Page(rootVisualElement);
            root.Clear();
            views.Clear();
            model = new SignInModel(WorkspaceContext.ProjectRoot, WorkspaceContext.Store, WorkspaceContext.Api, WorkspaceContext.ReprobeStore);

            var scroll = new ScrollView();
            scroll.AddToClassList("pingcore-grow");
            root.Add(scroll);
            scroll.Add(Ui.Title("PingCore"));
            scroll.Add(Ui.Note("Set up your game and its fleet in the PingCore panel or through MCP. Here you connect to that fleet, ship builds to it, and watch it run."));
            // First, because it explains a blank client scene in Play mode; the plugin never switches the profile itself.
            serverTargetBanner = Ui.Banner(string.Empty, BannerKind.Warning);
            serverTargetBanner.AddToClassList("pingcore-grow");
            serverTargetBox = Ui.Row(serverTargetBanner, Ui.Button("Open Build Profiles", ServerTargetCheck.OpenBuildProfiles));
            scroll.Add(serverTargetBox);
            problemBanner = Ui.Banner(string.Empty, BannerKind.Error);
            scroll.Add(problemBanner);
            overrideBanner = Ui.Banner(string.Empty, BannerKind.Warning);
            scroll.Add(overrideBanner);

            foreach (Section section in SectionBook.All)
            {
                var view = new SectionView(section, optional: section == Section.PlayerHosting);
                views[section] = view;
                scroll.Add(view.Root);
            }

            connect.Build(views[Section.Connect].Body, model, connectState, RefreshPage);
            ship.Build(views[Section.Ship].Body, connectState, connect, RefreshPage);
            status.Build(views[Section.Status].Body, connect, RefreshPage);
            // The push token Player hosting must never confuse with a heartbeat token: the one for the source Push pushes to.
            playerHosting.CdnSourceId = () =>
            {
                long pushed = PushTargetResolution.ForFleet(connectState.Facts, WorkspaceContext.LoadProject(out _).GameBranchId).CdnSourceId;
                return pushed > 0 ? pushed : connectState.Facts?.CdnSourceId ?? 0;
            };
            playerHosting.Build(views[Section.PlayerHosting].Body, RefreshPage);
            RefreshPage();

            // Once more after the Editor has finished loading (a window restored at startup is built before then), so the
            // chips never wait for a press.
            EditorApplication.delayCall += RefreshIfOpen;
        }

        private void RefreshIfOpen()
        {
            if (this != null)
            {
                RefreshPage();
            }
        }

        /// <summary>Works out every section's state again and shows it.</summary>
        public void RefreshPage()
        {
            if (views.Count == 0 || model == null)
            {
                return;
            }

            model.Reload();
            string serverTarget = ServerTargetCheck.WindowMessage(ServerTargetCheck.Current());
            Ui.SetBanner(serverTargetBanner, serverTarget, BannerKind.Warning);
            serverTargetBox.style.display = serverTarget == null ? DisplayStyle.None : DisplayStyle.Flex;
            Ui.SetBanner(problemBanner, Join(model.LoadProblem, model.EndpointProblem), BannerKind.Error);
            Ui.SetBanner(overrideBanner, model.OverrideNote, BannerKind.Warning);
            connect.Refresh();
            ship.Refresh();
            status.Refresh();
            playerHosting.Refresh();

            foreach (SectionState state in SectionBook.Evaluate(GatherFacts()))
            {
                views[state.Section].Apply(state);
            }
        }

        private SectionFacts GatherFacts()
        {
            EditorProjectSettings project = WorkspaceContext.LoadProject(out _);
            PingCoreClientSettings asset = ClientSettingsAsset.Find(out _);
            FleetFacts facts = connectState.Facts != null && connectState.Facts.FleetId == project.FleetId ? connectState.Facts : null;
            PushTarget target = PushTargetResolution.ForFleet(facts, project.GameBranchId);
            PingctlLocation pingctl = ship.Pingctl;
            return new SectionFacts
            {
                SignedIn = connect.SignedIn,
                SignedInLine = connect.Line,
                FleetCount = connectState.Fleets?.Count ?? -1,
                FleetId = project.FleetId,
                FleetName = facts?.FleetName,
                FleetResolved = facts != null,
                DeploymentCount = facts?.DeploymentCount ?? 0,
                PushProblem = facts == null ? null : target.Problem,
                BranchToPick = facts != null && target.Kind == PushTargetKind.PickBranch,
                BranchCount = facts == null ? 0 : target.Branches.Count,
                ReleaseProblem = facts == null ? null : ReleaseGate.Evaluate(facts, target.CdnSourceId).Refusal,
                OutsideThePlugin = facts != null && target.OutsideThePlugin,
                BuildProfileChosen = ship.ProfileChosen,
                PingctlFound = pingctl.Found,
                PingctlProblem = pingctl.Problem,
                CommunityAppSet = asset != null && !string.IsNullOrWhiteSpace(asset.CommunityAppPublicId),
                HeartbeatToken = PlayerHostingFold.TokenState(asset),
            };
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " " + b;
    }
}
