using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeaconRush.Session;
using Newtonsoft.Json.Linq;
using PingCore.Core.Handshake;
using PingCore.Fleet;
using PingCore.Fleet.Sessions;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// PingCore-hosted: the local SDK endpoint answered. Everything goes through the local SDK shim, never Discovery:
    /// <list type="bullet">
    /// <item>Boot order: <c>players = 0</c> before listening, the deliberate integrating write (after a 2xx <c>/ready</c>
    /// instead when the installed instrumentation asks, <see cref="BootPlan"/>); after listening
    /// <see cref="ServerInstrumentation.BeforeReadyAsync"/>, then <c>ReadyAsync</c>.</item>
    /// <item>Allocations on the watch stream open sessions that wait for their roster (<see cref="MatchContext"/>), with the
    /// settings <see cref="ServerInstrumentation.AllocatedSessionSettings"/> gives (by default <see cref="SessionSettings.Default"/>); the
    /// platform clearing one closes it. A self-allocation (a quick-play solo join claimed this idle game server through
    /// <see cref="HostedAdmissionEvidence"/>, opted in through <see cref="ApprovalOptions.ClaimIdleSessions"/>) opens a session
    /// like a backend allocation, with a short lobby (<see cref="SessionSettings.SelfAllocatedLobby"/>): its first player starts
    /// the match, and a claimed joiner that never arrives ends it at once (<c>GameServerRuntime.Connections.cs</c>).</item>
    /// <item>The <c>players</c> counter follows every join and leave (<see cref="PlayersCounterWriter"/>); <c>sessions</c> is never written.</item>
    /// <item>Admission evidence from the shim: holds, the roster, delivered backfills (<see cref="HostedAdmissionEvidence"/>, <see cref="BackfillWatcher"/>).</item>
    /// <item>With <c>joinInProgress</c>, the joinable record from match start (<c>HostedMode.Joinable.cs</c>).</item>
    /// <item>The end of a session: <c>EndSessionAsync</c>, or <c>ShutdownAsync</c> when the session's end mode says so.</item>
    /// </list>
    /// </summary>
    internal sealed partial class HostedMode : HostingModeBase
    {
        private readonly IFleetSdk fleet;
        private readonly PlayersCounterWriter counter;
        private readonly BootPlan plan;
        private readonly Stopwatch sinceBoot;
        private readonly BackfillWatcher backfills;
        private readonly BackfillExpectations expected = new BackfillExpectations();
        private GameServerRuntime runtime;

        public HostedMode(HostingSelection selection, IFleetSdk fleet, PlayersCounterWriter counter, BootPlan plan, Stopwatch sinceBoot)
            : base(selection)
        {
            this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            this.counter = counter ?? throw new ArgumentNullException(nameof(counter));
            this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
            this.sinceBoot = sinceBoot;
            backfills = new BackfillWatcher(fleet);
            Evidence = new HostedAdmissionEvidence(fleet, backfills);
        }

        public override IAdmissionEvidence Evidence { get; }

        public override bool UsesAllocations => true;

        public override void Attach(GameServerRuntime owner)
        {
            runtime = owner;
            fleet.AllocationReceived += OnAllocationReceived;
            fleet.AllocationCleared += OnAllocationCleared;
            backfills.BackfillReceived += OnBackfillReceived;
        }

        public override async Task<bool> BeforeListeningAsync(CancellationToken cancellationToken)
        {
            if (plan.FirstPlayersWrite == FirstPlayersWrite.BeforeListening)
            {
                await counter.WriteAsync("integrate");
            }

            return !cancellationToken.IsCancellationRequested;
        }

        public override async Task AfterListeningAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ServerInstrumentation.Current.BeforeReadyAsync(cancellationToken);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            // Stopped meanwhile (the hook, or a quit, took its time): never tell the platform a stopping game server is ready.
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            FleetCallResult ready = await fleet.ReadyAsync(cancellationToken);
            ServerEvents.Raise(ServerEvents.Ready, "status", ready.Status, "outcome", FleetEventBridge.Wire(ready.Outcome),
                "durationMs", sinceBoot == null ? 0L : sinceBoot.ElapsedMilliseconds);
            FleetEventBridge.RaiseIfFailed("ready", ready);
            if (ready.IsOk && plan.FirstPlayersWrite == FirstPlayersWrite.AfterReady)
            {
                await counter.WriteAsync("afterReady");
            }
        }

        public override bool IsCurrentSession(string allocationId)
        {
            AllocationInfo current = fleet.CurrentAllocation;
            return allocationId != null && fleet.IsHosted && fleet.State == FleetState.InSession && current != null
                && string.Equals(current.AllocationId, allocationId, StringComparison.Ordinal);
        }

        public override bool IsSelfAllocatedSession(string allocationId)
        {
            AllocationInfo current = fleet.CurrentAllocation;
            return IsCurrentSession(allocationId) && current.IsSelfAllocated;
        }

        public override void OnPlayersChanged(int players, string phase)
        {
            counter.Request(phase);
            UpdateJoinable(players);
        }

        public override void OnPhase(string allocationId, SessionPhase phase, SessionSettings settings, int players)
        {
            if (phase == SessionPhase.Match)
            {
                StartJoinable(allocationId, settings, players);
            }
            else if (phase == SessionPhase.Results)
            {
                // The match is over: no more seats to offer.
                StopJoinable(true);
            }
        }

        public override void OnDecided(AdmissionDecision decision, int players)
        {
            if (decision != null && decision.Approved && decision.Kind == JoinTicketKind.Backfill)
            {
                expected.Arrived(decision.AllocationId);
                UpdateJoinable(players);
            }
        }

        public override void OnSessionClosed()
        {
            // The session's end withdraws its record anyway; the keeper must not publish for it again.
            StopJoinable(false);
            expected.Clear();
        }

        public override async Task EndSessionAsync(SessionCommand command, CancellationToken cancellationToken)
        {
            string reason = SessionEndReasons.ToWire(command.Reason);
            if (command.Kind == SessionCommandKind.Shutdown)
            {
                FleetCallResult shutdown = await fleet.ShutdownAsync(cancellationToken);
                ServerEvents.Raise(ServerEvents.ShutdownRequested, "allocationId", command.AllocationId, "reason", reason,
                    "status", shutdown.Status, "outcome", FleetEventBridge.Wire(shutdown.Outcome));
                FleetEventBridge.RaiseIfFailed("shutdown", shutdown);
                return;
            }

            FleetCallResult ended = await fleet.EndSessionAsync(command.AllocationId, cancellationToken);
            ServerEvents.Raise(ServerEvents.SessionEnded, "allocationId", command.AllocationId, "reason", reason,
                "status", ended.Status, "outcome", FleetEventBridge.Wire(ended.Outcome));
            FleetEventBridge.RaiseIfFailed("endSession", ended);
        }

        public override void NotifyStopping()
        {
            StopJoinable(false);
            fleet.NotifyProcessStopping();
        }

        public override void Dispose()
        {
            StopJoinable(false);
            fleet.AllocationReceived -= OnAllocationReceived;
            fleet.AllocationCleared -= OnAllocationCleared;
            backfills.BackfillReceived -= OnBackfillReceived;
            backfills.Dispose();
        }

        private void OnAllocationReceived(AllocationInfo allocation)
        {
            MatchContext match = MatchContext.Parse(allocation);
            var ticketContexts = new List<JObject>();
            foreach (RosterEntry entry in match.Roster)
            {
                ticketContexts.Add(entry.Context);
            }

            SessionSettings settings = ServerInstrumentation.Ask(i => i.AllocatedSessionSettings(allocation.Context, ticketContexts, SessionSettings.Default),
                SessionSettings.Default) ?? SessionSettings.Default;
            if (allocation.IsSelfAllocated)
            {
                // A quick-play solo join claimed this game server: its player is already being let in.
                settings = settings.WithLobbyTimeout(SessionSettings.SelfAllocatedLobby);
            }

            var keys = new List<string>();
            if (allocation.Context != null)
            {
                foreach (KeyValuePair<string, JToken> entry in allocation.Context)
                {
                    keys.Add(entry.Key);
                }
            }

            ServerEvents.Raise(ServerEvents.Allocation,
                "allocationId", allocation.AllocationId,
                "contextKeys", keys.ToArray(),
                "matchmaker", match.IsMatchmaker,
                "selfAllocated", allocation.IsSelfAllocated,
                "queue", match.Queue,
                "rosterPlayers", match.RosterPlayers,
                "lobbySeconds", settings.LobbyTimeout.TotalSeconds,
                "matchSeconds", settings.MatchDuration.TotalSeconds,
                "resultsSeconds", settings.ResultsDuration.TotalSeconds,
                "endMode", SessionEndModes.ToWire(settings.EndMode),
                "joinInProgress", settings.JoinInProgress,
                "joinableOpenSeats", settings.JoinableOpenSeats,
                "bots", settings.Bots);
            currentMatch = match;
            runtime?.OpenSession(allocation.AllocationId, settings, match.RosterPlayers, false);
        }

        private void OnAllocationCleared(AllocationCleared cleared)
        {
            ServerEvents.Raise(ServerEvents.AllocationCleared, "allocationId", cleared.AllocationId,
                "reason", cleared.Reason == AllocationClearedReason.EndedByGame ? "endedByGame" : "clearedByPlatform");
            if (cleared.Reason == AllocationClearedReason.ClearedByPlatform)
            {
                runtime?.CloseClearedSession(cleared.AllocationId);
            }
        }

        private void OnBackfillReceived(BackfillContext backfill)
        {
            if (backfill == null || runtime == null || !string.Equals(backfill.SessionId, runtime.Director.AllocationId, StringComparison.Ordinal))
            {
                return;
            }

            expected.Add(backfill.AllocationId, backfill.RosterPlayers);
            ServerEvents.Raise(ServerEvents.Backfill, "allocationId", backfill.AllocationId, "sessionId", backfill.SessionId,
                "rosterPlayers", backfill.RosterPlayers, "queue", backfill.Queue, "expectedJoiners", expected.Pending);
            UpdateJoinable(runtime.Director.Players);
        }
    }
}
