namespace PingCore.Fleet
{
    /// <summary>
    /// The result of <see cref="IFleetSdk.AllocateSelfAsync"/>. <see cref="FleetCallResult.Outcome"/> says how the
    /// <c>POST /allocate</c> went; <see cref="Allocation"/> is what the watch stream showed afterwards.
    /// </summary>
    public sealed class SelfAllocationResult : FleetCallResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="outcome">How the call ended.</param>
        /// <param name="status">HTTP status, or 0 when nothing was sent or no answer arrived.</param>
        /// <param name="message">Why it was refused, or why the self-allocation was not seen; null on a confirmed success.</param>
        /// <param name="allocation">The self-allocation the watch stream carried, or the allocation that was already current when <paramref name="alreadyAllocated"/>.</param>
        /// <param name="alreadyAllocated">True when the shim refused locally because an allocation was already current.</param>
        /// <param name="awaitingEarlier">True when nothing was sent because an earlier self-allocation is still unconfirmed.</param>
        public SelfAllocationResult(FleetCallOutcome outcome, int status, string message, AllocationInfo allocation, bool alreadyAllocated, bool awaitingEarlier = false)
            : base(outcome, status, message)
        {
            Allocation = allocation;
            AlreadyAllocated = alreadyAllocated;
            AwaitingEarlier = awaitingEarlier;
        }

        /// <summary>
        /// On <see cref="FleetCallOutcome.Ok"/>: the self-allocation (<see cref="AllocationInfo.IsSelfAllocated"/>, id
        /// <c>self-&lt;ms&gt;</c>) as the watch stream delivered it, or null when the supervisor accepted the call but no
        /// frame carried a self-allocation within <see cref="FleetSdkOptions.SelfAllocationWait"/>. With
        /// <see cref="AlreadyAllocated"/>: the allocation that was already current. Otherwise null.
        /// </summary>
        public AllocationInfo Allocation { get; }

        /// <summary>
        /// True when nothing was sent because the view already carried an allocation (outcome
        /// <see cref="FleetCallOutcome.Rejected"/>, status 0). The supervisor would not refuse: its <c>/allocate</c>
        /// replaces the current allocation with the self-allocation, which would cut that session's players off.
        /// </summary>
        public bool AlreadyAllocated { get; }

        /// <summary>
        /// True when nothing was sent because an earlier <c>POST /allocate</c> is unconfirmed (its answer was lost, or its frame
        /// did not come) and <see cref="FleetSdkOptions.SelfAllocationGrace"/> has not passed, and no frame came during one more
        /// <see cref="FleetSdkOptions.SelfAllocationWait"/> (outcome <see cref="FleetCallOutcome.Rejected"/>, status 0). When the
        /// frame does come during that wait the result is <see cref="FleetCallOutcome.Ok"/> with status 0 and the
        /// self-allocation, so <see cref="IsConfirmed"/> is true.
        /// </summary>
        public bool AwaitingEarlier { get; }

        /// <summary>True when the call was accepted and the watch stream showed the self-allocation.</summary>
        public bool IsConfirmed => IsOk && Allocation != null && Allocation.IsSelfAllocated;
    }
}
