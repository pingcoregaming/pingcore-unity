namespace PingCore.Editor.Workspace.Api
{
    /// <summary>
    /// The outcome of one PingCore API call. <see cref="Ok"/> is true only for a 2xx JSON
    /// envelope with <c>error: false</c> whose <c>data</c> matched <typeparamref name="T"/>.
    /// Calls never throw for an HTTP or transport failure.
    /// </summary>
    public sealed class ApiResult<T>
        where T : class
    {
        private ApiResult(bool ok, T value, PluginError error, string message, int status)
        {
            Ok = ok;
            Value = value;
            Error = error;
            Message = message;
            HttpStatus = status;
        }

        /// <summary>True when the call succeeded and <see cref="Value"/> is set.</summary>
        public bool Ok { get; }

        /// <summary>The envelope's <c>data</c>, typed; null on failure.</summary>
        public T Value { get; }

        /// <summary>Why it failed; null on success.</summary>
        public PluginError Error { get; }

        /// <summary>The envelope's <c>message</c> on success (redacted), else null.</summary>
        public string Message { get; }

        /// <summary>The HTTP status, or 0 when no answer arrived.</summary>
        public int HttpStatus { get; }

        /// <summary>A success.</summary>
        public static ApiResult<T> Success(T value, string message = null, int status = 200)
        {
            return new ApiResult<T>(true, value, null, message, status);
        }

        /// <summary>A failure.</summary>
        public static ApiResult<T> Failure(PluginError error)
        {
            return new ApiResult<T>(false, null, error, null, error?.HttpStatus ?? 0);
        }
    }

    /// <summary>
    /// The value of a call whose answer the plugin acknowledges from the envelope alone: it never
    /// reads or keeps the answer's <c>data</c> (some answers carry fields the plugin has no use
    /// for, some of them sensitive).
    /// </summary>
    public sealed class ApiAck
    {
        public ApiAck(string message)
        {
            Message = message;
        }

        /// <summary>The envelope's message, redacted.</summary>
        public string Message { get; }
    }
}
