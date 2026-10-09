namespace PingCore.Fleet
{
    /// <summary>What happened, as the state machine sees it.</summary>
    internal enum FleetInputKind
    {
        /// <summary><c>StartAsync</c> begins.</summary>
        Start,

        /// <summary><c>StartAsync</c> read <c>GET /gameserver</c> 200.</summary>
        FirstView,

        /// <summary><c>StartAsync</c> gave up after its tries.</summary>
        StartFailed,

        /// <summary><c>POST /ready</c> answered 2xx.</summary>
        ReadyAccepted,

        /// <summary>A watch frame arrived.</summary>
        Frame,

        /// <summary><c>POST /shutdown</c> answered 2xx.</summary>
        ShutdownAccepted,

        /// <summary><c>NotifyProcessStopping</c>.</summary>
        ProcessStopping,

        /// <summary>The watch dropped and three reconnects in a row failed.</summary>
        WatchLost,
    }

    /// <summary>One input: its kind and, for a view, the Agones state and whether an allocation id is on it.</summary>
    internal readonly struct FleetInput
    {
        public FleetInput(FleetInputKind kind, string agonesState, bool hasAllocation)
        {
            Kind = kind;
            AgonesState = agonesState;
            HasAllocation = hasAllocation;
        }

        public FleetInputKind Kind { get; }

        public string AgonesState { get; }

        public bool HasAllocation { get; }

        public static FleetInput Of(FleetInputKind kind) => new FleetInput(kind, null, false);

        public static FleetInput View(FleetInputKind kind, GameServerSnapshot snapshot) => new FleetInput(kind, snapshot?.AgonesState, snapshot?.AllocationId != null);

        public override string ToString() => Kind == FleetInputKind.Frame || Kind == FleetInputKind.FirstView ? Kind + "(" + (AgonesState ?? "null") + (HasAllocation ? ", allocation" : string.Empty) + ")" : Kind.ToString();
    }

    /// <summary>
    /// The shim's lifecycle as a pure function. The watch frame is the source of truth for
    /// <see cref="FleetState.InSession"/>; local calls only predict.
    /// <list type="bullet">
    /// <item>Inert and Stopping are terminal.</item>
    /// <item>ProcessStopping: any other state to Stopping.</item>
    /// <item>Start: Starting or Unreachable to Starting.</item>
    /// <item>FirstView (from Starting): Shutdown to ShuttingDown; an allocation id or Allocated to InSession; Ready to Ready; anything else to NotReady.</item>
    /// <item>StartFailed: Starting to Unreachable.</item>
    /// <item>ReadyAccepted: NotReady to Ready.</item>
    /// <item>ShutdownAccepted: any non-terminal state to ShuttingDown.</item>
    /// <item>WatchLost: Ready or InSession to Unreachable.</item>
    /// <item>Frame: ShuttingDown stays (only Stopping leaves it); Shutdown to ShuttingDown; an allocation id or Allocated to InSession; otherwise InSession to Ready (NotReady when the view says Scheduled), Ready stays Ready (a stale Scheduled frame never un-readies), and NotReady or Unreachable follow the view (Ready to Ready, else NotReady).</item>
    /// </list>
    /// </summary>
    internal static class FleetStateMachine
    {
        public const string AgonesScheduled = "Scheduled";
        public const string AgonesReady = "Ready";
        public const string AgonesAllocated = "Allocated";
        public const string AgonesShutdown = "Shutdown";

        public static FleetState Next(FleetState state, FleetInput input)
        {
            if (state == FleetState.Inert || state == FleetState.Stopping)
            {
                return state;
            }

            switch (input.Kind)
            {
                case FleetInputKind.ProcessStopping:
                    return FleetState.Stopping;
                case FleetInputKind.Start:
                    return state == FleetState.Starting || state == FleetState.Unreachable ? FleetState.Starting : state;
                case FleetInputKind.FirstView:
                    return state == FleetState.Starting ? FromView(input) : state;
                case FleetInputKind.StartFailed:
                    return state == FleetState.Starting ? FleetState.Unreachable : state;
                case FleetInputKind.ReadyAccepted:
                    return state == FleetState.NotReady ? FleetState.Ready : state;
                case FleetInputKind.ShutdownAccepted:
                    return FleetState.ShuttingDown;
                case FleetInputKind.WatchLost:
                    return state == FleetState.Ready || state == FleetState.InSession ? FleetState.Unreachable : state;
                case FleetInputKind.Frame:
                    return FromFrame(state, input);
                default:
                    return state;
            }
        }

        private static FleetState FromView(FleetInput input)
        {
            if (input.AgonesState == AgonesShutdown)
            {
                return FleetState.ShuttingDown;
            }

            if (input.HasAllocation || input.AgonesState == AgonesAllocated)
            {
                return FleetState.InSession;
            }

            return input.AgonesState == AgonesReady ? FleetState.Ready : FleetState.NotReady;
        }

        private static FleetState FromFrame(FleetState state, FleetInput input)
        {
            if (state == FleetState.ShuttingDown || input.AgonesState == AgonesShutdown)
            {
                return FleetState.ShuttingDown;
            }

            if (input.HasAllocation || input.AgonesState == AgonesAllocated)
            {
                return FleetState.InSession;
            }

            switch (state)
            {
                case FleetState.InSession:
                    return input.AgonesState == AgonesScheduled ? FleetState.NotReady : FleetState.Ready;
                case FleetState.Ready:
                    return FleetState.Ready;
                default:
                    return input.AgonesState == AgonesReady ? FleetState.Ready : FleetState.NotReady;
            }
        }
    }
}
