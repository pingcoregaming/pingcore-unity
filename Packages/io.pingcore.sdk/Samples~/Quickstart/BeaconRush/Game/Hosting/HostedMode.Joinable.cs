using System;
using BeaconRush.Networking;
using BeaconRush.Session;
using PingCore.Fleet;
using PingCore.Fleet.Sessions;
using PingCore.Fleet.Wire;
using UnityEngine;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The joinable record of a hosted session with <c>joinInProgress</c>: from match start a
    /// <see cref="JoinableSessionKeeper"/> publishes the open seats
    /// (<c>max(0, min(8 - connected - expectedJoiners, playersCapacity - playersCount))</c>, <see cref="JoinablePlan"/>),
    /// republishes on every change and every 15 s, and withdraws at results. With
    /// <see cref="SessionSettings.JoinableOpenSeats"/> set it instead publishes once with exactly that many seats, as given
    /// (the keeper would clamp it). A 2xx is only local acceptance either way, and every attempt raises a <c>joinable</c> event.
    /// </summary>
    internal sealed partial class HostedMode
    {
        public const int JoinableTtlSeconds = 30;

        private MatchContext currentMatch;
        private JoinableSessionKeeper keeper;

        private void StartJoinable(string allocationId, SessionSettings settings, int players)
        {
            if (!settings.JoinInProgress || keeper != null || allocationId == null)
            {
                return;
            }

            MatchContext match = currentMatch != null && string.Equals(currentMatch.AllocationId, allocationId, StringComparison.Ordinal) ? currentMatch : null;
            // The queue comes only from the allocation context: a game server never names a queue of its own.
            string queue = match?.Queue;
            if (string.IsNullOrEmpty(queue))
            {
                Debug.LogWarning("[BeaconRush] joinInProgress is on but the allocation context names no queue; no joinable record is published");
                ServerEvents.Raise(ServerEvents.Joinable, "allocationId", allocationId, "call", "publish", "source", "none", "openSeats", null,
                    "status", 0, "outcome", "noQueue", "locallyAccepted", false);
                return;
            }

            int sessionSize = match?.SessionSize ?? 0;
            if (settings.JoinableOpenSeats.HasValue)
            {
                PublishOverride(allocationId, queue, sessionSize, settings.JoinableOpenSeats.Value);
                return;
            }

            keeper = JoinableSessionKeeper.Start(fleet, allocationId, queue, sessionSize, BeaconRushProtocol.MaxPlayers, JoinableTtlSeconds);
            keeper.Published += OnKeeperPublished;
            keeper.Withdrawn += OnKeeperWithdrawn;
            keeper.Update(players, expected.Pending);
        }

        private void UpdateJoinable(int players)
        {
            keeper?.Update(players, expected.Pending);
        }

        /// <param name="withdraw">True to withdraw the record now (results); false when the session's end withdraws it anyway.</param>
        private void StopJoinable(bool withdraw)
        {
            JoinableSessionKeeper running = keeper;
            keeper = null;
            if (running == null)
            {
                return;
            }

            if (withdraw)
            {
                // Stop withdraws; its Withdrawn event still reports the attempt.
                running.Stop();
            }
            else
            {
                running.Published -= OnKeeperPublished;
                running.Withdrawn -= OnKeeperWithdrawn;
                running.Dispose();
            }
        }

        private async void PublishOverride(string allocationId, string queue, int sessionSize, int openSeats)
        {
            try
            {
                var request = new JoinableSessionRequest
                {
                    Queue = queue,
                    OpenSeats = openSeats,
                    SessionSize = sessionSize > 0 ? sessionSize : (int?)null,
                    TtlSeconds = JoinableTtlSeconds,
                };
                JoinablePublishResult result = await fleet.PublishJoinableAsync(allocationId, request, default);
                RaiseJoinable(allocationId, "publish", "override", openSeats, result);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnKeeperPublished(JoinablePublishResult result)
        {
            JoinableSessionKeeper running = keeper;
            int? seats = result?.Record != null ? result.Record.OpenSeats : running?.OpenSeats;
            RaiseJoinable(running?.AllocationId ?? currentMatch?.AllocationId, "publish", "keeper", seats, result);
        }

        private void OnKeeperWithdrawn(FleetCallResult result)
        {
            RaiseJoinable(currentMatch?.AllocationId, "withdraw", "keeper", null, result);
        }

        private static void RaiseJoinable(string allocationId, string call, string source, int? openSeats, FleetCallResult result)
        {
            ServerEvents.Raise(ServerEvents.Joinable, "allocationId", allocationId, "call", call, "source", source, "openSeats", openSeats,
                "status", result == null ? 0 : result.Status, "outcome", result == null ? null : FleetEventBridge.Wire(result.Outcome),
                "locallyAccepted", result != null && result.IsOk);
            if (result != null)
            {
                FleetEventBridge.RaiseIfFailed("joinable", result);
            }
        }
    }
}
