using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// The fake's game server state and its GameServer view, built like <c>buildGameServerView</c>: state
    /// Shutdown, Allocated, Ready, Scheduled in that order; counters from the fleet block with the SDK value
    /// first; the allocation and the latest backfill on annotations. Counters echo with the fleet capacity
    /// fallback (<c>buildCounterView</c>).
    /// </summary>
    internal sealed partial class FakeLocalSdkEndpoint
    {
        private readonly Dictionary<string, (long? Count, long? Capacity)> sdkCounters = new Dictionary<string, (long?, long?)>(StringComparer.Ordinal);
        private readonly List<(string Name, long Capacity)> fleetCounters = new List<(string, long)> { ("players", 8), ("sessions", 1) };
        private readonly List<JObject> backfills = new List<JObject>();
        private string allocationId;
        private string allocationContextJson;

        /// <summary>Delivers an allocation (like a Discovery allocation frame) and pushes a frame.</summary>
        public void Allocate(string id, string contextJson)
        {
            lock (gate)
            {
                allocationId = id;
                allocationContextJson = contextJson ?? "{}";
            }

            PushView();
        }

        /// <summary>Clears the allocation without the game ending it (a membership reset) and pushes a frame.</summary>
        public void ClearAllocation()
        {
            lock (gate)
            {
                allocationId = null;
                allocationContextJson = null;
            }

            PushView();
        }

        /// <summary>Adds a delivered backfill (<c>buildBackfillsView</c> shape) and pushes a frame.</summary>
        public void AddBackfill(JObject backfill)
        {
            lock (gate)
            {
                backfills.Add(backfill);
            }

            PushView();
        }

        /// <summary>The GameServer view, built like <c>buildGameServerView</c>.</summary>
        public JObject BuildView()
        {
            lock (gate)
            {
                return BuildViewLocked();
            }
        }

        private JObject BuildViewLocked()
        {
            string agonesState = ShutdownRequested ? "Shutdown" : allocationId != null ? "Allocated" : Ready ? "Ready" : "Scheduled";
            var counters = new JObject();
            foreach ((string name, long capacity) in fleetCounters)
            {
                sdkCounters.TryGetValue(name, out (long? Count, long? Capacity) sdk);
                counters[name] = new JObject
                {
                    ["count"] = (sdk.Count ?? 0).ToString(),
                    ["capacity"] = (sdk.Capacity ?? capacity).ToString(),
                };
            }

            var annotations = new JObject();
            if (allocationId != null)
            {
                annotations["pingcore.io/allocation-id"] = allocationId;
                annotations["pingcore.io/allocation-context"] = allocationContextJson;
            }

            if (backfills.Count > 0)
            {
                JObject latest = backfills[backfills.Count - 1];
                annotations["pingcore.io/backfill-id"] = (string)latest["allocationId"];
                annotations["pingcore.io/backfill-context"] = (latest["context"] ?? new JObject()).ToString(Formatting.None);
            }

            return new JObject
            {
                ["object_meta"] = new JObject
                {
                    ["name"] = "gameserver-42",
                    ["namespace"] = "default",
                    ["labels"] = new JObject(),
                    ["annotations"] = annotations,
                },
                ["status"] = new JObject
                {
                    ["state"] = agonesState,
                    ["address"] = "203.0.113.1",
                    ["ports"] = new JArray(new JObject { ["name"] = "game", ["port"] = 27015 }),
                    ["counters"] = counters,
                },
            };
        }

        private (string Name, long Capacity)? FleetCounter(string name) =>
            fleetCounters.Where(c => c.Name == name).Select(c => ((string, long)?)c).FirstOrDefault();

        private JObject CounterView(string name, (string Name, long Capacity)? configured)
        {
            sdkCounters.TryGetValue(name, out (long? Count, long? Capacity) sdk);
            return new JObject
            {
                ["name"] = name,
                ["count"] = (sdk.Count ?? 0).ToString(),
                ["capacity"] = (sdk.Capacity ?? configured?.Capacity ?? 0).ToString(),
            };
        }
    }
}
