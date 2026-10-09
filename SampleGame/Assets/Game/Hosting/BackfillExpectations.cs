using System;
using System.Collections.Generic;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The backfill players a hosted session still waits for, pure: each delivered backfill adds its roster's players,
    /// each admitted <c>backfill</c> join of that backfill takes one away, and a closed session forgets them all. With
    /// the connected players it is the input of the joinable record
    /// (<c>JoinableSessionKeeper.Update(connected, expectedJoiners)</c>), so seats promised to an incoming backfill are
    /// never offered twice.
    /// </summary>
    public sealed class BackfillExpectations
    {
        private readonly Dictionary<string, int> remaining = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Backfill players delivered but not yet admitted.</summary>
        public int Pending
        {
            get
            {
                int total = 0;
                foreach (int count in remaining.Values)
                {
                    total += count;
                }

                return total;
            }
        }

        /// <summary>A backfill was delivered with <paramref name="rosterPlayers"/> players. False for an id already known (the watcher raises each once; a repeat changes nothing).</summary>
        public bool Add(string backfillAllocationId, int rosterPlayers)
        {
            if (string.IsNullOrEmpty(backfillAllocationId) || remaining.ContainsKey(backfillAllocationId))
            {
                return false;
            }

            remaining[backfillAllocationId] = Math.Max(0, rosterPlayers);
            return true;
        }

        /// <summary>One player of <paramref name="backfillAllocationId"/> was admitted. Never goes below zero; an unknown id changes nothing.</summary>
        public void Arrived(string backfillAllocationId)
        {
            if (backfillAllocationId != null && remaining.TryGetValue(backfillAllocationId, out int count) && count > 0)
            {
                remaining[backfillAllocationId] = count - 1;
            }
        }

        /// <summary>The session closed.</summary>
        public void Clear() => remaining.Clear();
    }
}
