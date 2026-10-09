using System.Collections.Generic;
using BeaconRush.Match;
using BeaconRush.Session;
using PingCore.Core.Handshake;
using Unity.Netcode;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The NGO side of <see cref="GameServerRuntime"/>: every approval decision becomes an <c>approval</c> event, approved
    /// players join the open session and the score board, and leaves are reported to the session and the mode (the
    /// <c>players</c> counter on a hosted game server, the heartbeat elsewhere). A joiner whose approval claimed this
    /// game server (the solo join) and who never becomes a player ends the self-allocated session at once when nobody
    /// else is in or on the way (<see cref="SessionDirector.OnClaimedJoinLost"/>).
    /// </summary>
    public sealed partial class GameServerRuntime
    {
        /// <summary>Approved joiners whose approval claimed the session, until their connection completes or closes.</summary>
        private readonly HashSet<ulong> claimedJoiners = new HashSet<ulong>();

        private void OnDecided(AdmissionDecision decision)
        {
            string name = decision.ConnectionId == NetworkManager.ServerClientId && mode != null && mode.HostPlays
                ? mode.HostDisplayName
                : gate?.TakeDisplayName(decision.PlayerId);
            if (decision.Approved)
            {
                displayNames[decision.ConnectionId] = name;
            }

            ServerEvents.Raise(ServerEvents.Approval, ApprovalEventFields(decision, mode?.Mode));
            mode?.OnDecided(decision, director.Players);
            if (decision.SessionClaim == SessionClaimOutcome.Claimed && decision.Approved)
            {
                claimedJoiners.Add(decision.ConnectionId);
            }
            else if (!decision.Approved && (decision.SessionClaim == SessionClaimOutcome.Claimed || decision.SessionClaim == SessionClaimOutcome.Failed))
            {
                // Refused after (or while) claiming: out of time, the connection closed, or stopping. The claim may have landed.
                ClaimedJoinLost(decision.ConnectionId);
            }
        }

        /// <summary>
        /// The <c>approval</c> event's keys and values: the game's fields, then any the installed <see cref="ServerInstrumentation"/>
        /// adds (read whole inside its guard, so an instrumentation that throws, even while it is enumerated, adds nothing and
        /// never stops the approval's bookkeeping).
        /// </summary>
        internal static object[] ApprovalEventFields(AdmissionDecision decision, GameHostingMode? hostingMode)
        {
            var fields = new List<object>
            {
                "clientId", (long)decision.ConnectionId,
                "decision", decision.Approved ? "accept" : "reject",
                "reason", decision.ReasonWire,
                "kind", decision.Kind.HasValue ? JoinTicketKinds.ToWire(decision.Kind.Value) : null,
                "mode", hostingMode.HasValue ? HostingModeSelector.Wire(hostingMode.Value) : null,
                "evidence", decision.EvidenceSource,
                "ms", decision.ElapsedMs,
                "ticketRef", decision.TicketRef,
                "sessionClaim", SessionClaimWire(decision.SessionClaim),
                "detail", decision.Detail,
            };
            List<KeyValuePair<string, object>> extra = ServerInstrumentation.Ask(i =>
            {
                IEnumerable<KeyValuePair<string, object>> added = i.ApprovalEventFields(decision);
                return added == null ? null : new List<KeyValuePair<string, object>>(added);
            }, null);
            if (extra != null)
            {
                foreach (KeyValuePair<string, object> field in extra)
                {
                    fields.Add(field.Key);
                    fields.Add(field.Value);
                }
            }

            return fields.ToArray();
        }

        /// <summary>A joiner that claimed the session will not become a player: end the self-allocated lobby if nobody else is in or holds a seat.</summary>
        private void ClaimedJoinLost(ulong connectionId)
        {
            claimedJoiners.Remove(connectionId);
            if (stopping || mode == null || !director.HasSession || !mode.IsSelfAllocatedSession(director.AllocationId))
            {
                return;
            }

            int otherSeats = approval == null ? 0 : approval.Ledger.Count - (approval.Ledger.Holds(connectionId) ? 1 : 0);
            AfterInput(director.OnClaimedJoinLost(otherSeats + claimedJoiners.Count));
        }

        /// <summary>The <c>approval</c> event's <c>sessionClaim</c>: null when no claim was made.</summary>
        internal static string SessionClaimWire(SessionClaimOutcome claim)
        {
            switch (claim)
            {
                case SessionClaimOutcome.Claimed: return "claimed";
                case SessionClaimOutcome.AllocatedMeanwhile: return "allocatedMeanwhile";
                case SessionClaimOutcome.Failed: return "failed";
                default: return null;
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            if (stopping || (clientId == NetworkManager.ServerClientId && (mode == null || !mode.HostPlays)) || !connectedClients.Add(clientId))
            {
                return;
            }

            claimedJoiners.Remove(clientId);
            SessionCommand command = director.OnPlayerJoined(clientId);
            if (!director.IsSessionPlayer(clientId))
            {
                // Approved for a session that closed before the connection completed: not a player, so its
                // disconnect raises nothing. A listen host's own client is never refused.
                if (clientId != NetworkManager.ServerClientId)
                {
                    connectedClients.Remove(clientId);
                    network.DisconnectClient(clientId, JoinRejectReasons.ToWire(JoinRejectReason.NotInSession));
                }

                return;
            }

            match?.AddPlayer(clientId, NameOf(clientId));
            ServerEvents.Raise(ServerEvents.PlayerJoined, "clientId", (long)clientId, "players", director.Players);
            mode?.OnPlayersChanged(director.Players, "join");
            AfterInput(command);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            displayNames.Remove(clientId);

            // NGO also reports connections it refused at approval; only approved players count.
            if (!connectedClients.Remove(clientId))
            {
                if (claimedJoiners.Contains(clientId))
                {
                    // Approved on a claim, gone before its connection completed.
                    ClaimedJoinLost(clientId);
                }

                return;
            }

            // A player of an earlier session (disconnected when it ended) is no longer one of the director's.
            SessionCommand command = director.OnPlayerLeft(clientId);
            match?.RemovePlayer(clientId);
            ServerEvents.Raise(ServerEvents.PlayerLeft, "clientId", (long)clientId, "players", director.Players);
            mode?.OnPlayersChanged(director.Players, "leave");
            AfterInput(command);
        }

        private void DisconnectAll(string reason)
        {
            if (network == null || !network.IsListening)
            {
                return;
            }

            foreach (ulong clientId in new List<ulong>(connectedClients))
            {
                if (clientId != NetworkManager.ServerClientId)
                {
                    network.DisconnectClient(clientId, reason);
                }
            }
        }

        private string NameOf(ulong clientId)
        {
            return displayNames.TryGetValue(clientId, out string name) ? name : null;
        }
    }
}
