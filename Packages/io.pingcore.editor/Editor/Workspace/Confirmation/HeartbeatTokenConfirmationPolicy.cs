using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Workspace.Api.Wire;

namespace PingCore.Editor.Workspace.Confirmation
{
    /// <summary>What the confirmation writer decided, one value per refusal it can give.</summary>
    public enum ConfirmationOutcome
    {
        /// <summary>Every check passed; the digest record was written.</summary>
        Confirmed,

        /// <summary>No heartbeat token is configured.</summary>
        TokenMissing,

        /// <summary>The configured token is not a <c>dsc_</c> token (the prefix and 16 or more letters or digits).</summary>
        TokenMalformed,

        /// <summary>The settings asset names no open app public id (<c>dscp_...</c>).</summary>
        AppIdMissing,

        /// <summary>The configured token equals a credential the plugin keeps in the credential store.</summary>
        TokenIsStoredCredential,

        /// <summary>A PingCore API call failed; <see cref="ConfirmationResult.Error"/> says why.</summary>
        ApiFailed,

        /// <summary>No Discovery app of the workspace has the configured public id.</summary>
        AppNotFound,

        /// <summary>The app is disabled.</summary>
        AppDisabled,

        /// <summary>The app is not an open-registration app (a private app's token never ships).</summary>
        AppNotOpen,

        /// <summary>An active token of another scope (<c>allocate</c> or <c>both</c>) ends in the same four characters.</summary>
        NonHeartbeatTokenSameLastFour,

        /// <summary>No active heartbeat-scope token ends in the configured token's last four characters.</summary>
        NoMatchingHeartbeatToken,

        /// <summary>Two or more active heartbeat-scope tokens end in the same four characters, so the match is ambiguous.</summary>
        SeveralMatchingHeartbeatTokens,
    }

    /// <summary>
    /// The pure rules of the build guard's confirmation writer: the local checks and the decision
    /// over the app the PingCore API returned. Messages never quote the token, not even its last
    /// four characters.
    /// <para>
    /// The limitation, stated plainly: <c>GET discovery/apps/{id}</c> returns each token masked to
    /// its prefix and last four characters (<c>dsc_...abcd</c>), never a digest. So the
    /// confirmation binds the token by app, open registration mode, heartbeat scope and those four
    /// characters only; it cannot prove the full token is the one the platform issued. The record
    /// it writes holds the full token's SHA-256, so the build guard still fails a build whose token
    /// differs from the confirmed one in any character.
    /// </para>
    /// </summary>
    public static class HeartbeatTokenConfirmationPolicy
    {
        /// <summary>The scope a shipped token must have.</summary>
        public const string HeartbeatScope = BuildGuardConfirmations.HeartbeatScope;

        /// <summary>The registration mode its app must have.</summary>
        public const string OpenMode = BuildGuardConfirmations.OpenRegistrationMode;

        /// <summary>The limitation, for the settings page.</summary>
        public const string LastFourLimitation =
            "PingCore shows app tokens only as their last four characters, so Confirm checks the app (open, enabled), "
            + "the token's scope (heartbeat) and those four characters, not the whole token. The record it writes holds "
            + "the whole token's SHA-256, so a build with any other token still fails.";

        private static readonly Regex TokenShape = new Regex("^dsc_[A-Za-z0-9]{16,}$", RegexOptions.CultureInvariant);
        private static readonly Regex PublicIdShape = new Regex("^dscp_[A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant);

        /// <summary>True for a <c>dsc_</c> token shape (never a <c>dscp_</c> public id).</summary>
        public static bool IsTokenShaped(string token) => token != null && TokenShape.IsMatch(token);

        /// <summary>True for a <c>dscp_</c> public id.</summary>
        public static bool IsPublicId(string publicId) => publicId != null && PublicIdShape.IsMatch(publicId);

        /// <summary>
        /// The checks made before anything is sent: a token is set and <c>dsc_</c>-shaped, the app id
        /// is a public id, and the token is none of <paramref name="storedSecrets"/> (the plugin's
        /// own credentials, which must never ship). Null when all pass.
        /// </summary>
        public static ConfirmationResult LocalCheck(string token, string appPublicId, IEnumerable<string> storedSecrets)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.TokenMissing,
                    "No open-registration heartbeat token is set, so there is nothing to confirm.",
                    "Paste the open app's heartbeat token into the field first, or leave it empty and give listen hosts the token through PINGCORE_DISCOVERY_TOKEN.");
            }

            if (!IsTokenShaped(token))
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.TokenMalformed,
                    "The heartbeat token is not a Discovery app token (dsc_ followed by 16 or more letters or digits).",
                    "Copy the heartbeat token of the open app from the workspace; a dscp_ public id is not a token.");
            }

            if (!IsPublicId(appPublicId))
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.AppIdMissing,
                    "The settings name no open app public id (dscp_...).",
                    "Fill in the community (open-registration) app public id, the app the token belongs to.");
            }

            foreach (string secret in storedSecrets ?? Enumerable.Empty<string>())
            {
                if (secret != null && string.Equals(secret, token, StringComparison.Ordinal))
                {
                    return ConfirmationResult.Refused(ConfirmationOutcome.TokenIsStoredCredential,
                        "The heartbeat token field holds one of the plugin's own credentials, which must never ship in a build.",
                        "Clear the field and paste the open app's heartbeat token instead.");
                }
            }

            return null;
        }

        /// <summary>The app in <paramref name="list"/> whose public id is <paramref name="appPublicId"/>, or null.</summary>
        public static DiscoveryAppListItem FindApp(DiscoveryAppListResponse list, string appPublicId)
        {
            return list?.Apps?.FirstOrDefault(a => a != null && string.Equals(a.PublicId, appPublicId, StringComparison.Ordinal));
        }

        /// <summary>The token's last four characters; compared, never shown.</summary>
        internal static string LastFour(string token) => token.Substring(token.Length - 4);

        /// <summary>
        /// The decision over <c>GET discovery/apps/{id}</c>: the app is the one asked for, enabled
        /// and open; no active token of another scope ends in the token's last four characters; and
        /// exactly one active heartbeat-scope token does. Inactive tokens are ignored. Null when the
        /// token may be confirmed.
        /// </summary>
        public static ConfirmationResult Decide(DiscoveryAppDetailResponse detail, string appPublicId, string token)
        {
            DiscoveryAppView app = detail?.App;
            if (app == null || !string.Equals(app.PublicId, appPublicId, StringComparison.Ordinal))
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.AppNotFound,
                    "The workspace did not return the app with that public id.",
                    "Check the community app public id against the workspace's Discovery apps.");
            }

            if (!app.Enabled)
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.AppDisabled,
                    $"Discovery app \"{app.Name}\" is disabled.",
                    "Enable the app in the workspace first; a disabled app accepts no heartbeats.");
            }

            if (!string.Equals(app.RegistrationMode, OpenMode, StringComparison.Ordinal))
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.AppNotOpen,
                    $"Discovery app \"{app.Name}\" is not an open-registration app (its registration mode is \"{app.RegistrationMode ?? "unknown"}\"). Only the open app's heartbeat token may ship in a build.",
                    "Use the community (open) app's id and its heartbeat token; a private app's token stays on the game server, in PINGCORE_DISCOVERY_TOKEN.");
            }

            string lastFour = LastFour(token);
            List<DiscoveryTokenView> sameFour = (detail.Tokens ?? new List<DiscoveryTokenView>())
                .Where(t => t != null && t.IsActive && t.MaskedToken != null
                    && t.MaskedToken.StartsWith("dsc_", StringComparison.Ordinal)
                    && t.MaskedToken.EndsWith(lastFour, StringComparison.Ordinal))
                .ToList();

            int otherScopes = sameFour.Count(t => !string.Equals(t.Scope, HeartbeatScope, StringComparison.Ordinal));
            if (otherScopes > 0)
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.NonHeartbeatTokenSameLastFour,
                    $"An active token of app \"{app.Name}\" with a scope other than heartbeat ends in the same four characters, so the configured token could be that one.",
                    "Never ship an allocate or both scope token. Paste the app's heartbeat-scope token, or revoke the other token if it is unused.");
            }

            int heartbeat = sameFour.Count;
            if (heartbeat == 0)
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.NoMatchingHeartbeatToken,
                    $"No active heartbeat token of app \"{app.Name}\" ends in the configured token's last four characters.",
                    "Paste the app's current heartbeat token; a revoked token or another app's token cannot be confirmed.");
            }

            if (heartbeat > 1)
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.SeveralMatchingHeartbeatTokens,
                    $"{heartbeat} active heartbeat tokens of app \"{app.Name}\" end in the same four characters, so the match is ambiguous.",
                    "Revoke the heartbeat tokens you no longer use, then confirm again.");
            }

            return null;
        }
    }
}
