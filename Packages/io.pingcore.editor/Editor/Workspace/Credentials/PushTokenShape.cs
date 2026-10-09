using System.Text.RegularExpressions;

namespace PingCore.Editor.Workspace.Credentials
{
    /// <summary>
    /// What a CDN source push token looks like, and the sentences "Use an existing push token" answers with, pure. A push
    /// token is <c>cdnpush_</c> followed by letters and digits (PingCore issues <c>cdnpush_</c> plus 48 hex digits);
    /// the check is the build guard's token shape, so a value it passes is one the guard and the redactor also know.
    /// No sentence here ever quotes the value, not even partly.
    /// </summary>
    public static class PushTokenShape
    {
        /// <summary>The prefix every push token starts with.</summary>
        public const string Prefix = "cdnpush_";

        /// <summary>A pasted value that is not push-token shaped.</summary>
        public const string NotATokenMessage = "That is not a push token: a push token starts with cdnpush_ followed by letters and digits. Nothing was stored.";

        /// <summary>The workspace answered 401 or 403 to the pasted token.</summary>
        public const string NotAcceptedMessage = "PingCore did not accept this push token. Nothing was stored.";

        /// <summary>What to do when the workspace refused the token.</summary>
        public const string NotAcceptedHint = "Check that you copied the whole token and that it was not replaced since; or let Push issue a new one.";

        private static readonly Regex Shape = new Regex("^cdnpush_[A-Za-z0-9]{16,256}$", RegexOptions.CultureInvariant);

        /// <summary>True when <paramref name="value"/>, with surrounding whitespace removed, is push-token shaped.</summary>
        public static bool Matches(string value) => value != null && Shape.IsMatch(value.Trim());
    }
}
