using System.Collections.Generic;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>One counter on the GameServer view, with the int64 strings parsed.</summary>
    public sealed class GameServerCounter
    {
        /// <param name="name">Counter name.</param>
        /// <param name="count">Parsed count, or null when the wire value was absent or not an integer.</param>
        /// <param name="capacity">Parsed capacity, or null when the wire value was absent or not an integer.</param>
        public GameServerCounter(string name, long? count, long? capacity)
        {
            Name = name;
            Count = count;
            Capacity = capacity;
        }

        /// <summary>Counter name.</summary>
        public string Name { get; }

        /// <summary>Parsed count, or null.</summary>
        public long? Count { get; }

        /// <summary>Parsed capacity, or null.</summary>
        public long? Capacity { get; }
    }

    /// <summary>
    /// The GameServer view as the shim last saw it (<c>GET /gameserver</c> or a watch frame),
    /// flattened. <see cref="View"/> is the wire body itself.
    /// </summary>
    public sealed class GameServerSnapshot
    {
        /// <summary>Creates a snapshot from a wire view. Missing parts become empty collections or null.</summary>
        public GameServerSnapshot(GameServerView view)
        {
            View = view ?? new GameServerView();
            GameServerObjectMeta meta = View.ObjectMeta;
            GameServerStatus status = View.Status;
            Name = meta?.Name;
            AgonesState = status?.State;
            Address = status?.Address;
            Ports = status?.Ports != null ? new List<GameServerPort>(status.Ports) : new List<GameServerPort>();
            Labels = meta?.Labels != null ? new Dictionary<string, string>(meta.Labels) : new Dictionary<string, string>();
            Annotations = meta?.Annotations != null ? new Dictionary<string, string>(meta.Annotations) : new Dictionary<string, string>();

            var counters = new Dictionary<string, GameServerCounter>();
            if (status?.Counters != null)
            {
                foreach (KeyValuePair<string, CounterView> entry in status.Counters)
                {
                    counters[entry.Key] = new GameServerCounter(
                        entry.Key,
                        LocalSdkValues.ParseInt64(entry.Value?.Count),
                        LocalSdkValues.ParseInt64(entry.Value?.Capacity));
                }
            }

            Counters = counters;
            AllocationId = NonEmpty(Annotations, LocalSdkValues.AllocationIdAnnotation);
            AllocationContextJson = NonEmpty(Annotations, LocalSdkValues.AllocationContextAnnotation);
            BackfillId = NonEmpty(Annotations, LocalSdkValues.BackfillIdAnnotation);
        }

        /// <summary><c>object_meta.name</c>: <c>gameserver-&lt;id&gt;</c>. The game server identity on a hosted game server.</summary>
        public string Name { get; }

        /// <summary><c>status.state</c>: <c>Scheduled</c>, <c>Ready</c>, <c>Allocated</c> or <c>Shutdown</c>.</summary>
        public string AgonesState { get; }

        /// <summary><c>status.address</c>: the public address players connect to.</summary>
        public string Address { get; }

        /// <summary><c>status.ports</c>.</summary>
        public IReadOnlyList<GameServerPort> Ports { get; }

        /// <summary><c>status.counters</c>, by name, parsed.</summary>
        public IReadOnlyDictionary<string, GameServerCounter> Counters { get; }

        /// <summary><c>object_meta.labels</c>.</summary>
        public IReadOnlyDictionary<string, string> Labels { get; }

        /// <summary><c>object_meta.annotations</c>.</summary>
        public IReadOnlyDictionary<string, string> Annotations { get; }

        /// <summary>The <c>pingcore.io/allocation-id</c> annotation, or null when there is none.</summary>
        public string AllocationId { get; }

        /// <summary>The raw <c>pingcore.io/allocation-context</c> annotation (JSON inside a string), or null.</summary>
        public string AllocationContextJson { get; }

        /// <summary>The <c>pingcore.io/backfill-id</c> annotation (the latest backfill), or null.</summary>
        public string BackfillId { get; }

        /// <summary>The wire body this snapshot was built from.</summary>
        public GameServerView View { get; }

        private static string NonEmpty(IReadOnlyDictionary<string, string> map, string key)
        {
            return map.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : null;
        }
    }
}
