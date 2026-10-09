namespace PingCore.Fleet
{
    /// <summary>One transition of <see cref="IFleetSdk.State"/>.</summary>
    public sealed class FleetStateChange
    {
        /// <param name="from">The state before.</param>
        /// <param name="to">The state after.</param>
        /// <param name="cause">What caused it: <c>start</c>, <c>ready</c>, <c>watch</c>, <c>shutdown</c>, <c>watchLost</c> or <c>processStopping</c>.</param>
        public FleetStateChange(FleetState from, FleetState to, string cause)
        {
            From = from;
            To = to;
            Cause = cause;
        }

        /// <summary>The state before.</summary>
        public FleetState From { get; }

        /// <summary>The state after.</summary>
        public FleetState To { get; }

        /// <summary>What caused the transition.</summary>
        public string Cause { get; }

        /// <inheritdoc />
        public override string ToString() => From + " -> " + To + " (" + Cause + ")";
    }
}
