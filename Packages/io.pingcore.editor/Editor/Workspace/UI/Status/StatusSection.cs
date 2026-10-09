using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Status
{
    /// <summary>One deployment row of Status, pure.</summary>
    public sealed class DeploymentStatusRow
    {
        public long DeploymentId { get; set; }

        public string Location { get; set; }

        public string Status { get; set; }

        public string GameServers { get; set; }

        public string BuildVersion { get; set; }

        /// <summary><c>EU West: active; 2 game servers (1 ready, 1 in session); running snapshot_x</c>.</summary>
        public override string ToString() => $"{Location}: {Status}; {GameServers}; running {BuildVersion}";

        /// <summary>The rows for a fleet's member deployments and its live state (null when the live read failed). Pure.</summary>
        public static IReadOnlyList<DeploymentStatusRow> Of(IEnumerable<FleetDeploymentView> deployments, FleetLiveResponse live)
        {
            return (deployments ?? Enumerable.Empty<FleetDeploymentView>())
                .Where(d => d != null)
                .Select(d =>
                {
                    FleetLiveDeploymentView l = live?.Deployments?.FirstOrDefault(x => x != null && x.BrandDeploymentId == d.BrandDeploymentId);
                    string servers = d.ServerCount == 1 ? "1 game server" : d.ServerCount + " game servers";
                    if (l != null)
                    {
                        servers += $" ({l.Ready} ready, {l.InSession} in session, {l.Draining} draining)";
                    }

                    return new DeploymentStatusRow
                    {
                        DeploymentId = d.BrandDeploymentId,
                        Location = d.LocationName ?? "location #" + d.LocationId,
                        Status = d.DeploymentStatus ?? "unknown",
                        GameServers = servers,
                        BuildVersion = string.IsNullOrEmpty(d.BuildVersion) ? "no build reported yet" : d.BuildVersion,
                    };
                })
                .ToList();
        }
    }

    /// <summary>
    /// Status: the fleet's live state and each deployment's location, game server count, status and build
    /// version, read only (<c>GET fleets/{id}</c> and <c>GET fleets/{id}/live</c>), each row with Open in panel.
    /// It reads once when the window opens, on Refresh (which has Connect read its facts of the fleet again too, so every
    /// chip follows) and on a focus of the window once its last read is older than <see cref="ConnectState.ReadAgainAfter"/>.
    /// A fleet with no deployment is this section's to say: the sentence and Add a deployment in the panel (the billable
    /// step stays in the panel). Deployments are added and scaled in the panel.
    /// </summary>
    public sealed class StatusSection
    {
        private ConnectSection connect;
        private Action changed;
        private SectionActions actions;
        private Label fleetLine;
        private VisualElement rows;
        private Label status;
        private long readOnceFor;
        private DateTime? lastReadUtc;
        private bool readNone;
        private Button addDeployment;

        /// <summary>Builds the section's body.</summary>
        public void Build(VisualElement body, ConnectSection connectView, Action onChanged)
        {
            connect = connectView;
            changed = onChanged;
            actions = new SectionActions();
            fleetLine = Ui.Status();
            body.Add(Ui.Row(Ui.WithClass(fleetLine, "pingcore-grow"),
                Ui.Button("Refresh", RefreshPressed),
                Ui.Button("Open in panel", () => OpenFleet())));
            rows = new VisualElement();
            body.Add(rows);
            addDeployment = Ui.Primary(Ui.Button("Add a deployment in the panel", OpenDeploy));
            body.Add(Ui.Row(addDeployment));
            status = Ui.Status();
            body.Add(status);
        }

        /// <summary>Reads the fleet once per window when signed in and a fleet is picked.</summary>
        public void Refresh()
        {
            if (rows == null)
            {
                return;
            }

            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            FleetFacts facts = connect.PickedFacts;
            bool none = fleetId > 0 && (facts != null ? facts.DeploymentCount == 0 : readNone);
            addDeployment.style.display = none ? DisplayStyle.Flex : DisplayStyle.None;
            if (fleetId <= 0)
            {
                fleetLine.text = "No fleet is picked under Connect.";
                rows.Clear();
                return;
            }

            if (connect.SignedIn && !actions.Busy && readOnceFor != fleetId)
            {
                readOnceFor = fleetId;
                readNone = false;
                actions.Run(status, ReadAsync, changed);
            }
        }

        /// <summary>The window's focus: reads again when the last read is older than <see cref="ConnectState.ReadAgainAfter"/>.</summary>
        public void ReadAgainIfOld()
        {
            if (rows == null || actions.Busy || !connect.SignedIn || WorkspaceContext.LoadProject(out _).FleetId <= 0 || !ConnectState.ShouldReadAgain(lastReadUtc, DateTime.UtcNow))
            {
                return;
            }

            actions.Run(status, ReadAsync, changed);
        }

        // Refresh reads this section's rows and Connect's facts of the fleet again (through Connect's own actions), so the chips follow.
        private void RefreshPressed()
        {
            actions.Run(status, ReadAsync, changed);
            connect.ReadAgainNow();
        }

        private async Task ReadAsync(CancellationToken ct)
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            IPingCoreApi api = WorkspaceContext.SignedInApi(out string problem);
            if (api == null || fleetId <= 0)
            {
                status.text = problem ?? "No fleet is picked under Connect.";
                return;
            }

            status.text = $"Reading fleet {fleetId}...";
            ApiResult<FleetDetailResponse> detail = await api.GetFleetAsync(fleetId, ct);
            lastReadUtc = DateTime.UtcNow;
            if (!detail.Ok)
            {
                status.text = ErrorText.Of(detail.Error);
                return;
            }

            ApiResult<FleetLiveResponse> live = await api.GetFleetLiveAsync(fleetId, ct);
            FleetView fleet = detail.Value.Fleet;
            fleetLine.text = $"{fleet?.Name ?? "Fleet #" + fleetId} ({fleet?.Status ?? "unknown"}), read {DateTime.Now:HH:mm:ss}.";
            rows.Clear();
            IReadOnlyList<DeploymentStatusRow> lines = DeploymentStatusRow.Of(detail.Value.Deployments, live.Ok ? live.Value : null);
            foreach (DeploymentStatusRow line in lines)
            {
                long id = line.DeploymentId;
                rows.Add(Ui.Row(Ui.WithClass(Ui.Status(line.ToString()), "pingcore-grow"),
                    Ui.Button("Open in panel", () => Application.OpenURL(PanelLinks.Deployment(connect.PanelBase, id)))));
            }

            readNone = lines.Count == 0;
            status.text = lines.Count == 0
                ? FleetResolution.NoDeploymentMessage
                : live.Ok ? string.Empty : "The live state could not be read: " + ErrorText.Of(live.Error);
        }

        private void OpenDeploy()
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetId > 0)
            {
                Application.OpenURL(PanelLinks.Deploy(connect.PanelBase, fleetId));
            }
        }

        private void OpenFleet()
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetId > 0)
            {
                Application.OpenURL(PanelLinks.Fleet(connect.PanelBase, fleetId));
            }
        }
    }
}
