using System;
using System.Collections.Generic;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// One observable step of the game server (boot, listening, ready, an allocation, a join...).
    /// Field values are strings, numbers, booleans, string arrays or null. No field ever carries a
    /// credential or a join ticket's <c>ticketId</c>.
    /// </summary>
    public sealed class ServerEvent
    {
        public ServerEvent(string name, IReadOnlyList<KeyValuePair<string, object>> fields)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Fields = fields ?? Array.Empty<KeyValuePair<string, object>>();
        }

        public string Name { get; }

        public IReadOnlyList<KeyValuePair<string, object>> Fields { get; }

        public object this[string key]
        {
            get
            {
                foreach (KeyValuePair<string, object> field in Fields)
                {
                    if (string.Equals(field.Key, key, StringComparison.Ordinal))
                    {
                        return field.Value;
                    }
                }

                return null;
            }
        }
    }

    /// <summary>
    /// The game server's event stream: a dedicated game server's, or a listen host's inside the player. The
    /// game raises; a listener in code the game does not reference (a log writer, an automation harness) subscribes
    /// to <see cref="Raised"/>. With none subscribed nothing is written anywhere. The names are the constants below;
    /// each <c>Raise</c> call site names its fields.
    /// </summary>
    public static class ServerEvents
    {
        public const string Boot = "boot";
        public const string GameServer = "gameserver";
        public const string Counter = "counter";
        public const string Listening = "listening";
        public const string Ready = "ready";
        public const string FleetState = "fleetState";
        public const string Allocation = "allocation";
        public const string AllocationCleared = "allocationCleared";
        public const string PlayerJoined = "playerJoined";
        public const string PlayerLeft = "playerLeft";
        public const string Approval = "approval";
        public const string SessionEnded = "sessionEnded";
        public const string ShutdownRequested = "shutdownRequested";
        public const string HealthPing = "healthPing";
        public const string SdkError = "sdkError";
        public const string EndpointClosed = "endpointClosed";
        public const string BootError = "bootError";
        public const string Stopping = "stopping";
        public const string Exit = "exit";
        public const string Hosting = "hosting";
        public const string Heartbeat = "heartbeat";
        public const string Echo = "echo";
        public const string Delist = "delist";
        public const string Phase = "phase";
        public const string MatchResult = "matchResult";
        public const string Joinable = "joinable";
        public const string Backfill = "backfill";

        /// <summary>Raised on the thread that raised the event (the Unity main thread).</summary>
        public static event Action<ServerEvent> Raised;

        /// <summary>Raises an event from alternating keys and values: <c>Raise("listening", "port", 7777, "transport", "udp")</c>.</summary>
        public static void Raise(string name, params object[] keysAndValues)
        {
            Action<ServerEvent> handler = Raised;
            if (handler == null)
            {
                return;
            }

            if (keysAndValues != null && keysAndValues.Length % 2 != 0)
            {
                throw new ArgumentException("keys and values must come in pairs", nameof(keysAndValues));
            }

            var fields = new List<KeyValuePair<string, object>>();
            for (int i = 0; keysAndValues != null && i < keysAndValues.Length; i += 2)
            {
                fields.Add(new KeyValuePair<string, object>((string)keysAndValues[i], keysAndValues[i + 1]));
            }

            handler(new ServerEvent(name, fields));
        }

        /// <summary>Drops every listener; called when the domain reloads and by tests.</summary>
        public static void ClearListeners() => Raised = null;
    }
}
