namespace PingCore.Fleet
{
    /// <summary>The result of one call to the local SDK endpoint. The shim never throws for an HTTP or transport failure; it returns one of these.</summary>
    public class FleetCallResult
    {
        /// <param name="outcome">How the call ended.</param>
        /// <param name="status">HTTP status, or 0 when no answer arrived.</param>
        /// <param name="message">The endpoint's <c>message</c> (or <c>error</c>) on a refusal, or the shim's own description; null on success.</param>
        public FleetCallResult(FleetCallOutcome outcome, int status, string message)
        {
            Outcome = outcome;
            Status = status;
            Message = message;
        }

        /// <summary>How the call ended.</summary>
        public FleetCallOutcome Outcome { get; }

        /// <summary>HTTP status, or 0.</summary>
        public int Status { get; }

        /// <summary>Why it was refused, or null.</summary>
        public string Message { get; }

        /// <summary>True for <see cref="FleetCallOutcome.Ok"/>.</summary>
        public bool IsOk => Outcome == FleetCallOutcome.Ok;

        /// <inheritdoc />
        public override string ToString() => Status > 0 ? Outcome + " " + Status : Outcome.ToString();
    }

    /// <summary>
    /// A counter read or update. <see cref="Count"/> and <see cref="Capacity"/> are parsed from
    /// the int64 strings the endpoint echoes; null when the call failed or a value did not parse.
    /// </summary>
    public sealed class CounterResult : FleetCallResult
    {
        /// <summary>Creates a counter result.</summary>
        public CounterResult(FleetCallOutcome outcome, int status, string message, string name, long? count, long? capacity)
            : base(outcome, status, message)
        {
            Name = name;
            Count = count;
            Capacity = capacity;
        }

        /// <summary>The counter name.</summary>
        public string Name { get; }

        /// <summary>The count after the call, or null.</summary>
        public long? Count { get; }

        /// <summary>The capacity after the call (the fleet block's when the game never set one), or null.</summary>
        public long? Capacity { get; }
    }
}
