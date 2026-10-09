using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PingCore.Fleet
{
    /// <summary>What one view changed about the allocation.</summary>
    internal sealed class AllocationChange
    {
        public AllocationCleared Cleared { get; set; }

        public AllocationInfo Received { get; set; }

        /// <summary>True when a new allocation's context annotation was present but not a JSON object.</summary>
        public bool ContextInvalid { get; set; }

        public bool IsEmpty => Cleared == null && Received == null;
    }

    /// <summary>
    /// Allocation dedupe and cleared detection, pure. An allocation id is raised once: a watch
    /// reconnect replays the current view, and an id seen before is never raised again (the
    /// fleet probe's <c>seenAllocations</c>). When the id leaves the view (or is replaced), it
    /// was ended by the game if <see cref="MarkEnding"/> named it, otherwise cleared by the platform.
    /// <para>
    /// The ending mark rule: the mark is set before <c>/ended</c> is sent and is cleared again only
    /// when the endpoint answered a refusal (<see cref="FleetCallOutcome.Rejected"/>, a 4xx or 5xx, or
    /// <see cref="FleetCallOutcome.Unsupported"/>, its 501 fallback): then the session did not end, and
    /// a later clear is the platform's. Every other outcome keeps it. On a transport failure,
    /// EndpointClosed or a cancellation the request may well have been handled (only the answer was
    /// lost), so a frame that later clears the allocation is still the game's end.
    /// </para>
    /// </summary>
    internal sealed class AllocationTracker
    {
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> ending = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The allocation on the last view, or null.</summary>
        public AllocationInfo Current { get; private set; }

        /// <summary>
        /// Records that the game is ending <paramref name="allocationId"/>. Called before the
        /// request is sent, because the frame that clears the allocation can arrive before the
        /// answer does.
        /// </summary>
        public void MarkEnding(string allocationId) => ending.Add(allocationId);

        /// <summary>Applies the ending mark rule to the outcome of the <c>/ended</c> call for <paramref name="allocationId"/>.</summary>
        public void OnEndAnswered(string allocationId, FleetCallOutcome outcome)
        {
            if (ClearsEndingMark(outcome))
            {
                ending.Remove(allocationId);
            }
        }

        /// <summary>True only for an answer that says the session did not end: Rejected or Unsupported.</summary>
        public static bool ClearsEndingMark(FleetCallOutcome outcome) => outcome == FleetCallOutcome.Rejected || outcome == FleetCallOutcome.Unsupported;

        public bool IsEnding(string allocationId) => ending.Contains(allocationId);

        /// <summary>Observes one view's allocation annotations.</summary>
        public AllocationChange Observe(string allocationId, string contextJson, DateTimeOffset now)
        {
            var change = new AllocationChange();
            string id = string.IsNullOrEmpty(allocationId) ? null : allocationId;
            string last = Current?.AllocationId;

            if (last != null && !string.Equals(id, last, StringComparison.Ordinal))
            {
                AllocationClearedReason reason = ending.Remove(last) ? AllocationClearedReason.EndedByGame : AllocationClearedReason.ClearedByPlatform;
                change.Cleared = new AllocationCleared(last, reason, now);
                Current = null;
            }

            if (id != null && Current == null)
            {
                JObject context = ParseContext(contextJson, out bool invalid);
                var info = new AllocationInfo(id, context, now, invalid);
                Current = info;
                if (seen.Add(id))
                {
                    change.Received = info;
                    change.ContextInvalid = invalid;
                }
            }

            return change;
        }

        /// <summary>The context annotation as an object; an empty object (and <paramref name="invalid"/> when it was present) otherwise.</summary>
        public static JObject ParseContext(string json, out bool invalid)
        {
            invalid = false;
            if (string.IsNullOrEmpty(json))
            {
                return new JObject();
            }

            JObject parsed = LocalSdkValues.TryParseObject(json);
            if (parsed == null)
            {
                invalid = true;
                return new JObject();
            }

            return parsed;
        }
    }
}
