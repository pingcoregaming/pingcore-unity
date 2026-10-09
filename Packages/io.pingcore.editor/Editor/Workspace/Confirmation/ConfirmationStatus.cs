using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Workspace.Confirmation
{
    /// <summary>Whether a configured heartbeat token would pass the build guard.</summary>
    public enum ConfirmationState
    {
        /// <summary>No token is configured; builds carry none and need no confirmation.</summary>
        NoToken,

        /// <summary>A recorded confirmation matches the token's digest.</summary>
        Confirmed,

        /// <summary>No recorded confirmation matches: every build carrying it fails <c>secret_literal</c>.</summary>
        Unconfirmed,
    }

    /// <summary>The per-token line of the settings page. Pure; never quotes the token.</summary>
    public static class ConfirmationStatus
    {
        /// <summary>The state of <paramref name="token"/> against <paramref name="confirmations"/>.</summary>
        public static ConfirmationState StateOf(string token, IReadOnlyList<BuildGuardConfirmation> confirmations)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return ConfirmationState.NoToken;
            }

            return Matching(token, confirmations) != null ? ConfirmationState.Confirmed : ConfirmationState.Unconfirmed;
        }

        /// <summary>One sentence for the settings page.</summary>
        public static string Describe(string token, IReadOnlyList<BuildGuardConfirmation> confirmations)
        {
            switch (StateOf(token, confirmations))
            {
                case ConfirmationState.NoToken:
                    return "No heartbeat token set: builds carry none, nothing to confirm.";
                case ConfirmationState.Confirmed:
                    BuildGuardConfirmation match = Matching(token, confirmations);
                    return $"Confirmed for build: heartbeat token of open app {match.appPublicId}, confirmed {match.confirmedAt}.";
                default:
                    return "Not confirmed: every build carrying this token fails the build guard (secret_literal). Press Confirm for build.";
            }
        }

        private static BuildGuardConfirmation Matching(string token, IReadOnlyList<BuildGuardConfirmation> confirmations)
        {
            string trimmed = token.Trim();
            return (confirmations ?? Array.Empty<BuildGuardConfirmation>()).FirstOrDefault(c => BuildGuardConfirmations.Confirms(c, trimmed));
        }
    }
}
