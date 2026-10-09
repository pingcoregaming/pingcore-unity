namespace PingCore.Fleet
{
    /// <summary>Severity of a <see cref="FleetLogEntry"/>.</summary>
    public enum FleetLogLevel
    {
        /// <summary>Normal operation, including an expected refused call during a stop.</summary>
        Info = 0,

        /// <summary>Something the game can survive: a bad watch line, a reconnect, a refused write.</summary>
        Warning = 1,

        /// <summary>A call failed in a way the game did not expect.</summary>
        Error = 2,
    }

    /// <summary>
    /// One diagnostic line from the shim. It names the call, its HTTP status and outcome and a
    /// plain message; it never carries request headers or a response body.
    /// </summary>
    public sealed class FleetLogEntry
    {
        /// <param name="level">Severity.</param>
        /// <param name="call">The operation, for example <c>ready</c>, <c>health</c>, <c>watch</c>, <c>counter</c>.</param>
        /// <param name="message">Plain-language description.</param>
        /// <param name="status">HTTP status, or 0 when there was none.</param>
        /// <param name="outcome">The call outcome, or null for an entry that is not about one call.</param>
        public FleetLogEntry(FleetLogLevel level, string call, string message, int status, FleetCallOutcome? outcome)
        {
            Level = level;
            Call = call;
            Message = message;
            Status = status;
            Outcome = outcome;
        }

        /// <summary>Severity.</summary>
        public FleetLogLevel Level { get; }

        /// <summary>The operation.</summary>
        public string Call { get; }

        /// <summary>Plain-language description.</summary>
        public string Message { get; }

        /// <summary>HTTP status, or 0.</summary>
        public int Status { get; }

        /// <summary>The call outcome, or null.</summary>
        public FleetCallOutcome? Outcome { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Status > 0 ? "[" + Level + "] " + Call + " " + Status + ": " + Message : "[" + Level + "] " + Call + ": " + Message;
        }
    }
}
