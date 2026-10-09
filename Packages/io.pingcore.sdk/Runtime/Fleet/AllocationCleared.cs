using System;

namespace PingCore.Fleet
{
    /// <summary>Why an allocation left the GameServer view.</summary>
    public enum AllocationClearedReason
    {
        /// <summary>The game ended the session with <see cref="IFleetSdk.EndSessionAsync"/>.</summary>
        EndedByGame = 0,

        /// <summary>
        /// The platform cleared it without the game ending it (a membership reset or detach).
        /// Stop serving that session.
        /// </summary>
        ClearedByPlatform = 1,
    }

    /// <summary>An allocation id disappeared from the GameServer view.</summary>
    public sealed class AllocationCleared
    {
        /// <param name="allocationId">The allocation that was cleared.</param>
        /// <param name="reason">Ended by the game or cleared by the platform.</param>
        /// <param name="clearedAt">When the shim saw it go, from its scheduler.</param>
        public AllocationCleared(string allocationId, AllocationClearedReason reason, DateTimeOffset clearedAt)
        {
            AllocationId = allocationId;
            Reason = reason;
            ClearedAt = clearedAt;
        }

        /// <summary>The allocation that was cleared.</summary>
        public string AllocationId { get; }

        /// <summary>Why it was cleared.</summary>
        public AllocationClearedReason Reason { get; }

        /// <summary>When the shim saw it go.</summary>
        public DateTimeOffset ClearedAt { get; }
    }
}
