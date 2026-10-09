namespace BeaconRush.Session
{
    /// <summary>What <see cref="SessionDirector"/> asks its host to do after an input.</summary>
    public enum SessionCommandKind
    {
        None,

        /// <summary>Report the end on the local SDK endpoint (<c>EndSessionAsync</c>), disconnect the players and stay up.</summary>
        EndSession,

        /// <summary>Ask the supervisor to recycle the game process (<c>ShutdownAsync</c>).</summary>
        Shutdown,
    }

    /// <summary>One output of <see cref="SessionDirector"/>: the command, the allocation it concerns and why.</summary>
    public readonly struct SessionCommand
    {
        public static readonly SessionCommand None = default;

        public SessionCommand(SessionCommandKind kind, string allocationId, SessionEndReason reason)
        {
            Kind = kind;
            AllocationId = allocationId;
            Reason = reason;
        }

        public SessionCommandKind Kind { get; }

        /// <summary>The allocation whose session ended; null for <see cref="SessionCommandKind.None"/>.</summary>
        public string AllocationId { get; }

        public SessionEndReason Reason { get; }

        public bool IsNone => Kind == SessionCommandKind.None;

        public override string ToString() =>
            IsNone ? "None" : Kind + "(" + AllocationId + ", " + SessionEndReasons.ToWire(Reason) + ")";
    }
}
