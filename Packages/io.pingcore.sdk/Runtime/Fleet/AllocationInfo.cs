using System;
using Newtonsoft.Json.Linq;

namespace PingCore.Fleet
{
    /// <summary>
    /// An allocation delivered on the GameServer view: the <c>pingcore.io/allocation-id</c>
    /// annotation and the <c>pingcore.io/allocation-context</c> annotation (JSON inside a
    /// string), parsed. Raised once per new allocation id, so a watch reconnect that replays
    /// the current view does not raise it again.
    /// </summary>
    public sealed class AllocationInfo
    {
        /// <param name="allocationId">The allocation id; also the session id for <see cref="IFleetSdk.EndSessionAsync"/>.</param>
        /// <param name="context">The parsed context; an empty object when the annotation was absent or not a JSON object.</param>
        /// <param name="receivedAt">When the shim first saw it, from its scheduler.</param>
        public AllocationInfo(string allocationId, JObject context, DateTimeOffset receivedAt)
            : this(allocationId, context, receivedAt, false)
        {
        }

        /// <param name="allocationId">The allocation id; also the session id for <see cref="IFleetSdk.EndSessionAsync"/>.</param>
        /// <param name="context">The parsed context; an empty object when the annotation was absent or not a JSON object.</param>
        /// <param name="receivedAt">When the shim first saw it, from its scheduler.</param>
        /// <param name="contextInvalid">True when the context annotation was present but did not parse as a JSON object.</param>
        public AllocationInfo(string allocationId, JObject context, DateTimeOffset receivedAt, bool contextInvalid)
        {
            AllocationId = allocationId;
            Context = context ?? new JObject();
            ReceivedAt = receivedAt;
            ContextInvalid = contextInvalid;
        }

        /// <summary>The allocation id.</summary>
        public string AllocationId { get; }

        /// <summary>The allocation context: whatever the allocating backend or the matchmaker sent. Never null.</summary>
        public JObject Context { get; }

        /// <summary>
        /// True when the <c>pingcore.io/allocation-context</c> annotation was present but did not parse as a
        /// JSON object (truncated, or not an object). <see cref="Context"/> is then empty, and
        /// <see cref="Sessions.MatchContext.HasRoster"/> stays true so a <c>match</c> join fails closed
        /// (<c>not_in_roster</c>) instead of reading the empty context as rosterless.
        /// </summary>
        public bool ContextInvalid { get; }

        /// <summary>When the shim first saw this allocation.</summary>
        public DateTimeOffset ReceivedAt { get; }

        /// <summary>True for a self-allocation (<c>POST /allocate</c>), whose id the supervisor prefixes with <c>self-</c>.</summary>
        public bool IsSelfAllocated => IsSelfAllocationId(AllocationId);

        /// <summary>True for the id of a self-allocation (<c>self-&lt;ms&gt;</c>).</summary>
        internal static bool IsSelfAllocationId(string allocationId) => allocationId != null && allocationId.StartsWith("self-", StringComparison.Ordinal);
    }
}
