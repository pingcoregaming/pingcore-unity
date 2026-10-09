using System;
using PingCore.Fleet;
using UnityEngine;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// Turns what the local SDK shim reports into <see cref="ServerEvents"/>: state transitions
    /// (<c>fleetState</c>), Agones state changes (<c>gameserver</c>), the shim's log (the Unity console,
    /// plus <c>healthPing</c> for health entries), counter results (<c>counter</c>) and failed calls
    /// (<c>sdkError</c>, or <c>endpointClosed</c> when the failure is expected during a stop). The session
    /// side of the shim's events (allocations) stays with <see cref="HostedMode"/>.
    /// </summary>
    internal sealed class FleetEventBridge
    {
        private IFleetSdk fleet;
        private bool sawGameServer;
        private string lastAgonesState;

        public void Attach(IFleetSdk sdk)
        {
            fleet = sdk;
            fleet.StateChanged += OnStateChanged;
            fleet.GameServerChanged += OnGameServerChanged;
        }

        public void Detach()
        {
            if (fleet == null)
            {
                return;
            }

            fleet.StateChanged -= OnStateChanged;
            fleet.GameServerChanged -= OnGameServerChanged;
            fleet = null;
        }

        /// <summary>The shim's log sink: the Unity console, and a <c>healthPing</c> event for each health entry.</summary>
        public static void OnFleetLog(FleetLogEntry entry)
        {
            string line = "[PingCore.Fleet] " + entry;
            switch (entry.Level)
            {
                case FleetLogLevel.Error:
                    Debug.LogError(line);
                    break;
                case FleetLogLevel.Warning:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }

            // The shim already logs only the first ping, every 30th and every failure.
            if (string.Equals(entry.Call, "health", StringComparison.Ordinal))
            {
                ServerEvents.Raise(ServerEvents.HealthPing, "status", entry.Status, "outcome", entry.Outcome.HasValue ? Wire(entry.Outcome.Value) : null);
            }
        }

        public static void RaiseCounter(string name, CounterResult result, long count, string phase)
        {
            ServerEvents.Raise(ServerEvents.Counter, "name", name, "count", result.Count ?? count, "status", result.Status,
                "outcome", Wire(result.Outcome), "phase", phase);
            RaiseIfFailed("counter", result);
        }

        public static void RaiseIfFailed(string call, FleetCallResult result)
        {
            switch (result.Outcome)
            {
                case FleetCallOutcome.Ok:
                case FleetCallOutcome.Inert:
                    return;
                case FleetCallOutcome.EndpointClosed:
                case FleetCallOutcome.Cancelled:
                    // Expected while the process stops: the supervisor closes the endpoint before it stops the game.
                    ServerEvents.Raise(ServerEvents.EndpointClosed, "call", call, "outcome", Wire(result.Outcome), "status", result.Status);
                    return;
                default:
                    ServerEvents.Raise(ServerEvents.SdkError, "call", call, "outcome", Wire(result.Outcome), "status", result.Status);
                    return;
            }
        }

        /// <summary>The event literal of an outcome: <c>ok</c>, <c>endpointClosed</c>, ...</summary>
        public static string Wire(FleetCallOutcome outcome)
        {
            string name = outcome.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static void OnStateChanged(FleetStateChange change)
        {
            ServerEvents.Raise(ServerEvents.FleetState, "from", change.From.ToString(), "to", change.To.ToString(), "cause", change.Cause);
        }

        private void OnGameServerChanged(GameServerSnapshot snapshot)
        {
            if (sawGameServer && string.Equals(snapshot.AgonesState, lastAgonesState, StringComparison.Ordinal))
            {
                return;
            }

            ServerEvents.Raise(ServerEvents.GameServer, "phase", sawGameServer ? "watch" : "start", "agonesState", snapshot.AgonesState, "name", snapshot.Name);
            sawGameServer = true;
            lastAgonesState = snapshot.AgonesState;
        }
    }
}
