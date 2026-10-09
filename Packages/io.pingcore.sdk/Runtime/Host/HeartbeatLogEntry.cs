namespace PingCore.Discovery.Host
{
    /// <summary>Severity of a <see cref="HeartbeatLogEntry"/>.</summary>
    public enum HeartbeatLogLevel
    {
        /// <summary>Normal operation: started, recovered, stopped, delisted.</summary>
        Info = 0,

        /// <summary>Something the game server survives: missed beats, a refusal, an unexpected answer form.</summary>
        Warning = 1,
    }

    /// <summary>
    /// One diagnostic line from the heartbeat tier. It names the operation, the HTTP status and a
    /// plain message; it never carries the token, a request header or a response body.
    /// </summary>
    public sealed class HeartbeatLogEntry
    {
        /// <param name="level">Severity.</param>
        /// <param name="call">The operation: <c>heartbeat</c>, <c>delist</c> or <c>verify</c>.</param>
        /// <param name="message">Plain-language description.</param>
        /// <param name="status">HTTP status, or 0 when there was none.</param>
        public HeartbeatLogEntry(HeartbeatLogLevel level, string call, string message, int status)
        {
            Level = level;
            Call = call;
            Message = message;
            Status = status;
        }

        /// <summary>Severity.</summary>
        public HeartbeatLogLevel Level { get; }

        /// <summary>The operation.</summary>
        public string Call { get; }

        /// <summary>Plain-language description.</summary>
        public string Message { get; }

        /// <summary>HTTP status, or 0.</summary>
        public int Status { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Status > 0 ? "[" + Level + "] " + Call + " " + Status + ": " + Message : "[" + Level + "] " + Call + ": " + Message;
        }
    }
}
