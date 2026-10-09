using System;

namespace PingCore.Editor.Workspace.Api
{
    /// <summary>What went wrong, in a form the UI can branch on.</summary>
    public enum PluginErrorKind
    {
        /// <summary>No key is stored for the workspace, or the workspace answered 401.</summary>
        NotSignedIn,

        /// <summary>The workspace answered 403. <see cref="PluginError.Permission"/> names the brand permission the route needs (from the route table, never parsed from text).</summary>
        MissingBrandPermission,

        /// <summary>The workspace answered 404.</summary>
        NotFound,

        /// <summary>The workspace answered 409 (for example a release already in progress).</summary>
        Conflict,

        /// <summary>The workspace answered 429. <see cref="PluginError.RetryAfter"/> holds its <c>Retry-After</c> when it sent one.</summary>
        RateLimited,

        /// <summary>A tool the step needs is not installed or not found (pingctl, the Linux build module).</summary>
        ToolMissing,

        /// <summary>A child process ended with a non-zero exit code (<see cref="PluginError.ExitCode"/>), timed out or could not be started.</summary>
        ChildFailed,

        /// <summary>No usable answer arrived: a network failure, a timeout, or a redirect (never followed).</summary>
        Transport,

        /// <summary>The answer was not the platform's JSON envelope, or its <c>data</c> did not have the expected shape.</summary>
        Envelope,

        /// <summary>A well-formed envelope with <c>error: true</c> on any other status (including the HTTP 200 <c>error: true</c> some handlers send). <see cref="PluginError.Message"/> carries its message and <see cref="PluginError.Reason"/> its <c>data.reason</c>.</summary>
        Rejected,

        /// <summary>The plugin refused before sending anything (an http URL, a token-shaped argument, a malformed key).</summary>
        Refused,

        /// <summary>The caller cancelled.</summary>
        Cancelled,
    }

    /// <summary>
    /// One failure of one step. Every text in it has already passed the redactor, so it may be
    /// shown and logged as it is. It never holds a response body, a header or a URL with ids.
    /// </summary>
    public sealed class PluginError
    {
        /// <param name="step">The step that failed, as in the route table (<c>verify-key</c>, <c>release</c>, <c>push</c>).</param>
        /// <param name="kind">What went wrong.</param>
        /// <param name="message">A sentence for the developer.</param>
        /// <param name="hint">What to do about it, or null.</param>
        public PluginError(string step, PluginErrorKind kind, string message, string hint)
        {
            Step = step ?? string.Empty;
            Kind = kind;
            Message = message ?? string.Empty;
            Hint = hint;
        }

        /// <summary>The step that failed.</summary>
        public string Step { get; }

        /// <summary>What went wrong.</summary>
        public PluginErrorKind Kind { get; }

        /// <summary>A sentence for the developer, already redacted.</summary>
        public string Message { get; }

        /// <summary>What to do about it, or null.</summary>
        public string Hint { get; }

        /// <summary>The HTTP status of the answer, or 0 when there was none.</summary>
        public int HttpStatus { get; set; }

        /// <summary>For <see cref="PluginErrorKind.MissingBrandPermission"/>: the brand permission the route needs.</summary>
        public string Permission { get; set; }

        /// <summary>For <see cref="PluginErrorKind.RateLimited"/>: the wait the workspace asked for, when it said.</summary>
        public TimeSpan? RetryAfter { get; set; }

        /// <summary>For <see cref="PluginErrorKind.ChildFailed"/>: the exit code, when the child ran.</summary>
        public int? ExitCode { get; set; }

        /// <summary>The envelope's <c>data.reason</c> when it carried one (for example <c>release_in_progress</c>).</summary>
        public string Reason { get; set; }

        /// <summary>
        /// True only when the client failed before the request reached the transport (a refused
        /// argument, no key, a cancel before sending), so the workspace cannot have acted on it.
        /// False whenever the request may have gone out, including a transport failure.
        /// </summary>
        public bool NotSent { get; set; }

        /// <summary>One line: step, kind, message and hint.</summary>
        public override string ToString()
        {
            string hint = string.IsNullOrEmpty(Hint) ? string.Empty : " " + Hint;
            return $"{Step}: {Kind}: {Message}{hint}";
        }
    }
}
