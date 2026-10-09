using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Connect;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// The Release row: releases the fleet onto the snapshot the last push published, never an assumed name, and
    /// watches the rollout until it ends (the planner's release rules). Release is the only Ship step that needs
    /// deployments (<see cref="ReleaseGate"/>): with none it is disabled with the reason and the panel's deploy page,
    /// and a fleet whose deployments deliver from another CDN source than the branch pushes to is warned about. A release still moving when the Editor
    /// stopped is offered again with Continue; Stop ends the watch (the release carries on), Cancel release cancels
    /// it in the workspace, and Acknowledge dismisses a failed one so it no longer holds scale-down.
    /// </summary>
    public sealed partial class ShipSection
    {
        private const string SnapshotKeyPrefix = "PingCore.Ship.Snapshot.";
        private const string SnapshotSourceKeyPrefix = "PingCore.Ship.SnapshotSource.";

        private Label releaseLine;
        private Label releaseWarning;
        private Button addDeployment;
        private Label progressLabel;
        private Button stopButton;
        private Button cancelReleaseButton;
        private Button acknowledgeButton;
        private Button continueButton;

        private void BuildReleaseArea(VisualElement body)
        {
            releaseLine = Ui.Status();
            addDeployment = Ui.Button("Add a deployment in the panel", OpenDeploy);
            body.Add(Ui.Row(Ui.WithClass(releaseLine, "pingcore-grow"), addDeployment));
            releaseWarning = Ui.Banner(string.Empty, BannerKind.Warning);
            body.Add(releaseWarning);
        }

        private void BuildReleaseProgress(VisualElement body)
        {
            progressLabel = Ui.Status();
            body.Add(progressLabel);
            stopButton = Ui.Button("Stop", StopRunning);
            stopButton.tooltip = "Stops the running step. A release already started keeps rolling; Continue watches it again.";
            cancelReleaseButton = Ui.Button("Cancel release", CancelRelease);
            acknowledgeButton = Ui.Button("Acknowledge", () => StartRow(releaseRow, AcknowledgeAsync));
            continueButton = Ui.Button("Continue", () => StartRow(releaseRow, ContinueAsync));
            body.Add(Ui.Row(stopButton, cancelReleaseButton, acknowledgeButton, continueButton));
        }

        private void RefreshRelease()
        {
            if (releaseLine == null)
            {
                return;
            }

            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            string snapshot = LastSnapshot(fleetId);
            FleetFacts facts = Facts();

            // Release is the one step that needs deployments; before the fleet is read it is not gated (it reads the
            // fleet again when pressed).
            ReleaseGateResult gate = facts == null ? null : ReleaseGate.Evaluate(facts, PushedSource(fleetId, snapshot));
            releaseRow?.Gate(gate != null && !gate.Allowed ? gate.Refusal : null);
            addDeployment.style.display = gate != null && gate.OfferDeploy ? DisplayStyle.Flex : DisplayStyle.None;
            Ui.SetBanner(releaseWarning, gate?.Warning ?? string.Empty, BannerKind.Warning);
            releaseLine.text = gate != null && !gate.Allowed
                ? gate.Refusal
                : snapshot == null
                    ? "Release releases the snapshot your last push published: push first."
                    : $"Release moves fleet #{fleetId} onto {snapshot}, the snapshot your last push published.";

            DeployState state = commands?.State;
            ResumeOffer offer = commands?.ResumeOffer;
            bool running = commands?.Events?.RunStatus == PipelineRunStatus.Running;
            ReleaseProgressView progress = commands?.Events?.ReleaseProgress;
            progressLabel.text = progress == null
                ? (offer != null && !running ? offer.Message : string.Empty)
                : $"Release {progress.ReleaseId} to {progress.TargetBuildVersion}: {progress.State}"
                  + (string.IsNullOrEmpty(progress.Blocked) ? string.Empty : $" (waiting: {progress.Blocked})")
                  + (progress.Locations.Count == 0 ? string.Empty : "\n" + string.Join("\n", progress.Locations.Select(l => l.ToString())));
            stopButton.style.display = running || busyRow != null ? DisplayStyle.Flex : DisplayStyle.None;
            cancelReleaseButton.style.display = state != null && state.ReleaseUnfinished ? DisplayStyle.Flex : DisplayStyle.None;
            acknowledgeButton.style.display = !running && state != null && state.ReleaseState == "failed" && !state.Acknowledged ? DisplayStyle.Flex : DisplayStyle.None;
            continueButton.style.display = !running && offer != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private async Task ReleaseAsync(ShipRowView row, CancellationToken ct)
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            string snapshot = LastSnapshot(fleetId);
            if (snapshot == null)
            {
                row.Raise(ShipRowEvent.Fail, "There is no pushed snapshot to release yet: press Push first.");
                return;
            }

            // The fleet as it is now: a deployment added or removed in the panel since Connect read it counts.
            IPingCoreApi api = SignedInApi(row);
            if (api == null)
            {
                return;
            }

            (FleetFacts facts, PluginError error) = await ConnectState.Read(api, fleetId, ct);
            if (facts == null)
            {
                row.Raise(ShipRowEvent.Fail, ErrorText.Of(error));
                return;
            }

            connect.Use(facts);
            ReleaseGateResult gate = ReleaseGate.Evaluate(facts, PushedSource(fleetId, snapshot, facts));
            if (!gate.Allowed)
            {
                row.Raise(ShipRowEvent.Fail, gate.Refusal);
                return;
            }

            if (gate.Warning != null)
            {
                row.Running(gate.Warning);
            }

            var request = new DeployRequest { Release = true, BuildVersion = snapshot, FleetId = fleetId };
            await RunAsync(row, request, false, ct, gate.Warning);
        }

        private void OpenDeploy()
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetId > 0)
            {
                Application.OpenURL(PanelLinks.Deploy(connectSection?.PanelBase, fleetId));
            }
        }

        private async Task ContinueAsync(ShipRowView row, CancellationToken ct)
        {
            if (commands.ResumeOffer == null)
            {
                row.Raise(ShipRowEvent.Fail, "There is no unfinished run to continue.");
                return;
            }

            await RunAsync(row, new DeployRequest { FleetId = commands.ResumeOffer.FleetId }, true, ct);
        }

        // Cancel release while a row runs (the release being watched): stop the watch first, then send the cancel.
        private void CancelRelease()
        {
            if (busyRow == null && commands?.Events?.RunStatus != PipelineRunStatus.Running)
            {
                StartRow(releaseRow, CancelReleaseAsync);
                return;
            }

            cancelReleaseWhenStopped = true;
            shipStatus.text = "Stopping the watch, then cancelling the release...";
            StopRunning();
        }

        private async Task CancelReleaseAsync(ShipRowView row, CancellationToken ct)
        {
            PluginError error = await commands.CancelReleaseAsync(ct);
            row.Raise(error == null ? ShipRowEvent.Succeed : ShipRowEvent.Fail, error == null ? "Cancel sent; press Continue to watch the release end." : ErrorText.Of(error));
        }

        private async Task AcknowledgeAsync(ShipRowView row, CancellationToken ct)
        {
            PluginError error = await commands.AcknowledgeReleaseAsync(ct);
            row.Raise(error == null ? ShipRowEvent.Succeed : ShipRowEvent.Fail, error == null ? "The failed release is acknowledged; it no longer holds scale-down." : ErrorText.Of(error));
        }

        // The snapshot the last push to this fleet published: kept for this Editor session, else the saved run's.
        private string LastSnapshot(long fleetId)
        {
            string remembered = fleetId > 0 ? SessionState.GetString(SnapshotKeyPrefix + fleetId, string.Empty) : string.Empty;
            if (remembered.Length > 0)
            {
                return remembered;
            }

            DeployState state = commands?.State;
            return state != null && state.FleetId == fleetId && state.WantPush && state.Pushed && !string.IsNullOrEmpty(state.Snapshot) ? state.Snapshot : null;
        }

        // The CDN source the snapshot was pushed to (kept with it, else the saved run's); before any push is known, the
        // source the picked branch would push to. Release compares it with the source the fleet's deployments use.
        private long PushedSource(long fleetId, string snapshot, FleetFacts facts = null)
        {
            if (fleetId > 0 && snapshot != null)
            {
                string remembered = SessionState.GetString(SnapshotSourceKeyPrefix + fleetId, string.Empty);
                int bar = remembered.LastIndexOf('|');
                if (bar > 0 && remembered.Substring(0, bar) == snapshot && long.TryParse(remembered.Substring(bar + 1), out long id) && id > 0)
                {
                    return id;
                }

                DeployState state = commands?.State;
                if (state != null && state.FleetId == fleetId && state.Pushed && state.Snapshot == snapshot && state.CdnSourceId > 0)
                {
                    return state.CdnSourceId;
                }
            }

            return PushTargetResolution.ForFleet(facts ?? Facts(), WorkspaceContext.LoadProject(out _).GameBranchId).CdnSourceId;
        }

        private static void RememberSnapshot(long fleetId, string snapshot, long cdnSourceId)
        {
            if (fleetId > 0 && !string.IsNullOrEmpty(snapshot))
            {
                SessionState.SetString(SnapshotKeyPrefix + fleetId, snapshot);
                SessionState.SetString(SnapshotSourceKeyPrefix + fleetId, cdnSourceId > 0 ? snapshot + "|" + cdnSourceId : string.Empty);
            }
        }
    }
}
