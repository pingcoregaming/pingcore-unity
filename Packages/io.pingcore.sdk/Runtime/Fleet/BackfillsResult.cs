using System.Collections.Generic;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>The result of <see cref="IFleetSdk.GetBackfillsAsync"/>: every backfill Discovery delivered into the running session and not yet ended, oldest first.</summary>
    public sealed class BackfillsResult : FleetCallResult
    {
        /// <summary>Creates a backfills result.</summary>
        public BackfillsResult(FleetCallOutcome outcome, int status, string message, IReadOnlyList<BackfillView> backfills)
            : base(outcome, status, message)
        {
            Backfills = backfills ?? new List<BackfillView>();
        }

        /// <summary>The live backfills; empty when there are none or the call failed. Never null.</summary>
        public IReadOnlyList<BackfillView> Backfills { get; }
    }
}
