using PingCore.Editor.BuildGuard;
using PingCore.Editor.Workspace.Api;

namespace PingCore.Editor.Workspace.Confirmation
{
    /// <summary>
    /// The outcome of one Confirm for build. Its texts never quote the token. On success it carries
    /// the record that was written (a digest, never the token).
    /// </summary>
    public sealed class ConfirmationResult
    {
        private ConfirmationResult(ConfirmationOutcome outcome, string message, string hint, PluginError error, BuildGuardConfirmation written)
        {
            Outcome = outcome;
            Message = message ?? string.Empty;
            Hint = hint;
            Error = error;
            Written = written;
        }

        /// <summary>True when the record was written.</summary>
        public bool Ok => Outcome == ConfirmationOutcome.Confirmed;

        /// <summary>What was decided.</summary>
        public ConfirmationOutcome Outcome { get; }

        /// <summary>A sentence for the developer.</summary>
        public string Message { get; }

        /// <summary>What to do about it, or null.</summary>
        public string Hint { get; }

        /// <summary>For <see cref="ConfirmationOutcome.ApiFailed"/>: the call's error (already redacted).</summary>
        public PluginError Error { get; }

        /// <summary>On success, the record written to <c>ProjectSettings/PingCoreBuildGuard.json</c>.</summary>
        public BuildGuardConfirmation Written { get; }

        internal static ConfirmationResult Refused(ConfirmationOutcome outcome, string message, string hint)
            => new ConfirmationResult(outcome, message, hint, null, null);

        internal static ConfirmationResult Failed(PluginError error)
            => new ConfirmationResult(ConfirmationOutcome.ApiFailed, error?.Message ?? "The workspace call failed.", error?.Hint, error, null);

        internal static ConfirmationResult Confirmed(BuildGuardConfirmation written, string appName)
            => new ConfirmationResult(ConfirmationOutcome.Confirmed,
                $"Confirmed: the token is the heartbeat token of open app \"{appName}\". Builds may now carry it.", null, null, written);

        /// <summary>One line: message and hint.</summary>
        public override string ToString() => string.IsNullOrEmpty(Hint) ? Message : Message + " " + Hint;
    }
}
