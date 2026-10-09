using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.UI.Common
{
    /// <summary>
    /// How every window shows a <see cref="PluginError"/>: its message, the brand permission a 403
    /// needs, the wait a 429 asked for, and its hint. Already-redacted texts are redacted again on
    /// the way out, so nothing raw is ever shown or logged. Pure.
    /// </summary>
    public static class ErrorText
    {
        /// <summary>One or two sentences for the UI; empty for null.</summary>
        public static string Of(PluginError error)
        {
            if (error == null)
            {
                return string.Empty;
            }

            string text = string.IsNullOrEmpty(error.Message) ? error.Kind.ToString() : error.Message;
            if (error.Kind == PluginErrorKind.MissingBrandPermission && !string.IsNullOrEmpty(error.Permission))
            {
                text += $" This needs the brand permission {error.Permission}.";
            }

            if (error.Kind == PluginErrorKind.RateLimited && error.RetryAfter.HasValue)
            {
                text += $" Try again in {(int)System.Math.Ceiling(error.RetryAfter.Value.TotalSeconds)} s.";
            }

            if (!string.IsNullOrEmpty(error.Hint))
            {
                text += " " + error.Hint;
            }

            return Redactor.PatternsOnly.Redact(text.Trim());
        }
    }
}
