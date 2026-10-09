using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Connect
{
    /// <summary>
    /// Connect: sign in (<see cref="SignInPanel"/>), then pick the fleet this project ships to from <c>GET fleets</c>
    /// (name, game, number of deployments). There is no create button: games and fleets are set up in the panel or
    /// through MCP. With no fleet in the workspace it says so and links to the panel's Fleets page. A fleet with no
    /// deployment is picked like any other: Status says so and links to the panel's deploy page.
    /// Picking a fleet writes its id (and its game's) into <c>ProjectSettings/PingCoreEditor.json</c> and its
    /// Discovery app's public id into the client settings asset (<see cref="ConnectState.Write"/>).
    /// </summary>
    public sealed class ConnectSection
    {
        private readonly SignInPanel signIn = new SignInPanel();
        private ConnectState state;
        private SignInModel model;
        private Action changed;
        private SectionActions actions;
        private VisualElement fleetBox;
        private DropdownField fleetChoice;
        private Label fleetLine;
        private Button openFleets;
        private Button openFleet;
        private Label status;
        private bool listedOnce;
        private long resolvedOnceFor;

        /// <summary>True while a key is stored and the workspace has not refused it.</summary>
        public bool SignedIn => signIn.SignedIn;

        /// <summary>The signed-in line.</summary>
        public string Line => signIn.Line;

        /// <summary>The panel's address for links.</summary>
        public string PanelBase => SignInPanel.PanelBase(model?.Endpoint?.Host);

        /// <summary>Builds the section's body.</summary>
        public void Build(VisualElement body, SignInModel signInModel, ConnectState shared, Action onChanged)
        {
            model = signInModel;
            state = shared;
            changed = onChanged;
            actions = new SectionActions();
            signIn.Build(body, signInModel, onChanged);

            fleetBox = new VisualElement();
            body.Add(fleetBox);
            fleetBox.Add(Ui.Section("Fleet"));
            fleetChoice = new DropdownField("Fleet", new List<string>(), 0);
            fleetChoice.AddToClassList("pingcore-grow");
            fleetChoice.RegisterValueChangedCallback(e => Pick(e.newValue));
            fleetBox.Add(Ui.Row(fleetChoice, Ui.Button("Refresh", () => actions.Run(status, RefreshAsync, Changed))));
            fleetLine = Ui.Status();
            fleetBox.Add(fleetLine);
            openFleets = Ui.Button("Open Fleets in the panel", () => Application.OpenURL(PanelLinks.Fleets(PanelBase)));
            openFleet = Ui.Button("Open the fleet in the panel", () => Open(id => PanelLinks.Fleet(PanelBase, id)));
            fleetBox.Add(Ui.Row(openFleets, openFleet));
            status = Ui.Status();
            fleetBox.Add(status);
            Refresh();
        }

        /// <summary>The picked fleet's facts as Connect last read them, or null.</summary>
        public FleetFacts PickedFacts
        {
            get
            {
                long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
                return state?.Facts != null && state.Facts.FleetId == fleetId ? state.Facts : null;
            }
        }

        /// <summary>
        /// The window's focus: reads the picked fleet again when its facts are older than <see cref="ConnectState.ReadAgainAfter"/>,
        /// so a deployment added in the panel (or removed) reaches every chip without a press.
        /// </summary>
        public void ReadAgainIfOld()
        {
            if (fleetBox == null || actions == null || actions.Busy || !signIn.SignedIn || !ConnectState.ShouldReadAgain(state.LastReadUtc, DateTime.UtcNow))
            {
                return;
            }

            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetId > 0)
            {
                resolvedOnceFor = fleetId;
                actions.Run(status, ct => ResolveAsync(fleetId, ct), Changed);
            }
        }

        /// <summary>
        /// Reads the picked fleet again now (Status's Refresh), so the chips follow what Status shows. It runs through
        /// Connect's own actions: a pick pressed meanwhile is refused with "Still working on the last request", and this
        /// read is skipped while Connect is busy with one of its own.
        /// </summary>
        public void ReadAgainNow()
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetBox == null || actions == null || actions.Busy || !signIn.SignedIn || fleetId <= 0)
            {
                return;
            }

            resolvedOnceFor = fleetId;
            actions.Run(status, ct => ResolveAsync(fleetId, ct), Changed);
        }

        /// <summary>The window's <c>OnDisable</c>.</summary>
        public void Disable() => signIn.Disable();

        /// <summary>Shows the fleets and the picked fleet; lists the fleets and reads the picked one once per window.</summary>
        public void Refresh()
        {
            if (fleetBox == null)
            {
                return;
            }

            signIn.Refresh();
            bool signedIn = signIn.SignedIn;
            fleetBox.style.display = signedIn ? DisplayStyle.Flex : DisplayStyle.None;
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            ShowFleets(fleetId);
            ShowFacts(fleetId);
            if (!signedIn || actions.Busy)
            {
                return;
            }

            if (!listedOnce)
            {
                listedOnce = true;
                actions.Run(status, ListAsync, Changed);
            }
            else if (fleetId > 0 && resolvedOnceFor != fleetId && (state.Facts == null || state.Facts.FleetId != fleetId))
            {
                resolvedOnceFor = fleetId;
                actions.Run(status, ct => ResolveAsync(fleetId, ct), Changed);
            }
        }

        private void ShowFleets(long fleetId)
        {
            IReadOnlyList<FleetView> fleets = state.Fleets ?? Array.Empty<FleetView>();
            List<string> choices = fleets.Select(FleetResolution.Choice).ToList();
            fleetChoice.choices = choices;
            FleetView current = fleets.FirstOrDefault(f => f.FleetId == fleetId);
            fleetChoice.SetValueWithoutNotify(current != null ? FleetResolution.Choice(current) : fleetId > 0 ? $"fleet #{fleetId}" : "(pick the fleet)");
            openFleets.style.display = state.Fleets != null && state.Fleets.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (state.Fleets != null && state.Fleets.Count == 0)
            {
                fleetLine.text = FleetResolution.NoFleetMessage;
            }
        }

        private void ShowFacts(long fleetId)
        {
            FleetFacts facts = state.Facts != null && state.Facts.FleetId == fleetId ? state.Facts : null;
            openFleet.style.display = fleetId > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (state.Fleets != null && state.Fleets.Count == 0)
            {
                return;
            }

            if (fleetId <= 0)
            {
                fleetLine.text = "Pick the fleet this project ships to. Its game and Discovery app come with it.";
                return;
            }

            if (facts == null)
            {
                fleetLine.text = state.Problem ?? $"Fleet #{fleetId}: press Refresh to read it.";
                return;
            }

            string deployments = facts.DeploymentCount == 1 ? "1 deployment" : facts.DeploymentCount + " deployments";
            string source = facts.CdnSourceId > 0 ? $", delivering from CDN source #{facts.CdnSourceId}" : string.Empty;
            fleetLine.text = $"{facts.FleetName} (#{facts.FleetId}) runs {facts.GameName ?? "game #" + facts.GameId}; Discovery app {facts.AppPublicId ?? "none"}; {deployments}{source}."
                + (facts.GameProblem == null ? string.Empty : "\n" + facts.GameProblem);
        }

        private void Pick(string choice)
        {
            FleetView fleet = (state.Fleets ?? Array.Empty<FleetView>()).FirstOrDefault(f => FleetResolution.Choice(f) == choice);
            if (fleet == null)
            {
                return;
            }

            actions.Run(status, async ct =>
            {
                IPingCoreApi api = Api();
                if (api == null)
                {
                    return;
                }

                status.text = $"Reading fleet {fleet.Name}...";
                (FleetFacts facts, PluginError error) = await ConnectState.Read(api, fleet.FleetId, ct);
                if (facts == null)
                {
                    status.text = ErrorText.Of(error);
                    return;
                }

                status.text = ConnectState.Write(WorkspaceContext.ProjectRoot, facts);
                resolvedOnceFor = facts.FleetId;
                state.Use(facts);
            }, Changed);
        }

        private async Task RefreshAsync(CancellationToken ct)
        {
            await ListAsync(ct);
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetId > 0)
            {
                await ResolveAsync(fleetId, ct);
            }
        }

        private async Task ListAsync(CancellationToken ct)
        {
            IPingCoreApi api = Api();
            if (api == null)
            {
                return;
            }

            await state.ListFleetsAsync(api, ct);
            status.text = state.Problem ?? string.Empty;
        }

        private async Task ResolveAsync(long fleetId, CancellationToken ct)
        {
            IPingCoreApi api = Api();
            if (api == null)
            {
                return;
            }

            await state.ResolveAsync(api, fleetId, ct, () => WorkspaceContext.LoadProject(out _).FleetId == fleetId);
            if (WorkspaceContext.LoadProject(out _).FleetId != fleetId)
            {
                // Another fleet was picked while this one was read: its answer was dropped, and nothing is written for it.
                return;
            }

            if (state.Facts != null && state.Facts.FleetId == fleetId && !string.IsNullOrWhiteSpace(state.Facts.AppPublicId) && ClientSettingsAsset.Find(out _) != null)
            {
                // An existing asset follows the fleet, so an app id changed in the panel reaches the next player build;
                // a read never creates the asset (only picking a fleet does).
                ConnectState.Write(WorkspaceContext.ProjectRoot, state.Facts);
            }

            status.text = state.Problem ?? string.Empty;
        }

        private IPingCoreApi Api()
        {
            IPingCoreApi api = WorkspaceContext.SignedInApi(out string problem);
            if (api == null)
            {
                status.text = problem;
            }

            return api;
        }

        private void Open(Func<long, string> link)
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetId > 0)
            {
                Application.OpenURL(link(fleetId));
            }
        }

        private void Changed()
        {
            Refresh();
            changed?.Invoke();
        }
    }
}
