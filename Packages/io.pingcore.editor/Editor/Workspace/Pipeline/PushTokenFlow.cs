using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// The fleet's CDN source push token: issued once, kept in the credential store, never shown. Issuing
    /// replaces the source's token at once for every holder (a script, CI, another machine, a teammate), so
    /// the plugin issues one only when the store holds none for that source, after the developer confirmed
    /// <see cref="IssueConfirmationText"/>, and replaces a stored one only when the developer asks for exactly
    /// that (the stored token stopped working) and confirmed the same sentence. The new value goes straight
    /// from the answer into the store (<see cref="IPingCoreApi.IssuePushTokenAsync"/>). A token issued elsewhere (the
    /// panel, CI, a teammate) can be kept instead with <see cref="UseExistingAsync"/>, which replaces nobody's token.
    /// </summary>
    public sealed class PushTokenFlow
    {
        private readonly ICredentialStore store;
        private readonly string host;

        /// <param name="store">The credential store.</param>
        /// <param name="host">The workspace host the token belongs to.</param>
        public PushTokenFlow(ICredentialStore store, string host)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            if (!CredentialTargets.IsValidHost(host))
            {
                throw new ArgumentException("The workspace host must be a lower-case DNS name.", nameof(host));
            }

            this.host = host;
        }

        /// <summary>The sentence the confirmation dialog shows before a token is issued: it names the cost.</summary>
        public static string IssueConfirmationText(long sourceId)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Issuing a push token for CDN source {0} replaces its current token at once: any other copy stops working, "
                + "for example in CI, a script, another machine or a teammate's Editor, until it is given the new one. "
                + "The new token is kept in your credential store and never shown. "
                + "If a teammate or CI already pushes to this source, use their token instead (Use an existing push token). Issue it?",
                sourceId);
        }

        /// <summary>The sentence shown before a pasted token replaces the one already stored for the source.</summary>
        public static string ReplaceStoredConfirmationText(long sourceId)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "A push token for CDN source {0} is already in your credential store. Keep the one you pasted instead? "
                + "The stored one is only forgotten here, not revoked: anyone else holding it can still push.",
                sourceId);
        }

        /// <summary>
        /// "Use an existing push token": keeps a token issued elsewhere (the panel, CI, a teammate) for
        /// <paramref name="expectedSourceId"/>, the CDN source the branch pushes to, under the same target the issue path
        /// uses, in the persistent store. In order: the token's shape (never echoed), the store readable, then
        /// <c>GET cdn-sources/push/info</c> sent with the token itself (<see cref="IPingCoreApi.CheckPushTokenAsync"/>):
        /// PingCore must accept it and name the same source; then, when a token is stored already,
        /// <paramref name="confirmReplace"/> (<see cref="ReplaceStoredConfirmationText"/>); then the write. Nothing is
        /// stored on any refusal or failure, and no message or receipt carries the token. Issues nothing, so no other
        /// holder is affected.
        /// </summary>
        public async Task<ApiResult<SecretReceipt>> UseExistingAsync(IPingCoreApi api, long expectedSourceId, string pastedToken, Func<bool> confirmReplace, CancellationToken cancellationToken)
        {
            if (api == null)
            {
                throw new ArgumentNullException(nameof(api));
            }

            if (expectedSourceId <= 0)
            {
                return Refused("The branch names no CDN source, so there is no push token to keep. Nothing was stored.");
            }

            string token = pastedToken?.Trim();
            if (!PushTokenShape.Matches(token))
            {
                return Refused(PushTokenShape.NotATokenMessage);
            }

            if (api.Endpoint.Host != host)
            {
                return Refused("The API client belongs to another workspace. Nothing was stored.");
            }

            PushTokenPresence presence = Presence(expectedSourceId, out string storeProblem);
            if (presence == PushTokenPresence.Unreadable)
            {
                return Refused(storeProblem + " Nothing was stored.");
            }

            ApiResult<Api.Wire.PushInfoResponse> checkedToken = await api.CheckPushTokenAsync(token, cancellationToken);
            if (!checkedToken.Ok)
            {
                return ApiResult<SecretReceipt>.Failure(checkedToken.Error);
            }

            long sourceId = checkedToken.Value?.Source?.SourceId ?? 0;
            if (sourceId != expectedSourceId)
            {
                return Refused(string.Format(CultureInfo.InvariantCulture, "This push token belongs to CDN source #{0}, not #{1}. Nothing was stored.", sourceId, expectedSourceId));
            }

            // Asked again: a token stored while the check was on the network is never replaced without the confirmation.
            presence = Presence(expectedSourceId, out storeProblem);
            if (presence == PushTokenPresence.Unreadable)
            {
                return Refused(storeProblem + " Nothing was stored.");
            }

            if (presence == PushTokenPresence.Stored && (confirmReplace == null || !confirmReplace()))
            {
                return Refused("Nothing was stored: the push token already in your credential store stays.");
            }

            string target = CredentialTargets.PushToken(host, expectedSourceId);
            try
            {
                store.Write(target, token);
            }
            catch (CredentialStoreException e)
            {
                return Refused("The push token could not be stored: " + Redaction.Redactor.PatternsOnly.Redact(e.Message));
            }

            return ApiResult<SecretReceipt>.Success(new SecretReceipt(target, store.Kind, null), "Push token kept.");
        }

        /// <summary>
        /// Whether a push token for <paramref name="sourceId"/> is stored, asked with <c>Exists</c>, never by reading it.
        /// A store that cannot be read is <see cref="PushTokenPresence.Unreadable"/>, never "none": issuing then would
        /// replace every other holder's token and the new one could not be kept either.
        /// </summary>
        public PushTokenPresence Presence(long sourceId, out string problem)
        {
            problem = null;
            if (sourceId <= 0)
            {
                return PushTokenPresence.None;
            }

            try
            {
                return store.Exists(CredentialTargets.PushToken(host, sourceId)) ? PushTokenPresence.Stored : PushTokenPresence.None;
            }
            catch (CredentialStoreException e)
            {
                problem = "Your credential store could not be read, so the plugin cannot tell whether a push token is stored: "
                    + Redaction.Redactor.PatternsOnly.Redact(e.Message);
                return PushTokenPresence.Unreadable;
            }
        }

        /// <summary>
        /// Issues a token for <paramref name="sourceId"/>, only when <paramref name="confirmed"/> (the developer accepted
        /// <see cref="IssueConfirmationText"/>), and only when none is stored unless <paramref name="replaceStored"/>
        /// (the developer pressed Replace because the stored one stopped working). The receipt names where it was
        /// stored, never the value.
        /// </summary>
        public async Task<ApiResult<SecretReceipt>> IssueAsync(IPingCoreApi api, long sourceId, bool confirmed, bool replaceStored, CancellationToken cancellationToken)
        {
            if (api == null)
            {
                throw new ArgumentNullException(nameof(api));
            }

            if (sourceId <= 0)
            {
                return Refused("The branch names no CDN source, so there is no push token to issue.");
            }

            if (!confirmed)
            {
                return Refused("A push token is issued only after you confirm that any other copy of the current one stops working.");
            }

            PushTokenPresence presence = Presence(sourceId, out string storeProblem);
            if (presence == PushTokenPresence.Unreadable)
            {
                return Refused(storeProblem + " No push token was issued: issuing would stop every other copy working.");
            }

            if (!replaceStored && presence == PushTokenPresence.Stored)
            {
                return Refused($"A push token for CDN source {sourceId} is already stored, so none was issued. Replace it only if it stopped working.");
            }

            if (api.Endpoint.Host != host)
            {
                return Refused("The API client belongs to another workspace.");
            }

            return await api.IssuePushTokenAsync(sourceId, store, cancellationToken);
        }

        /// <summary>The stored token, for the push child's environment only; null when none is stored.</summary>
        internal string ReadForChild(long sourceId)
        {
            return store.Read(CredentialTargets.PushToken(host, sourceId))?.Secret;
        }

        private static ApiResult<SecretReceipt> Refused(string message)
            => ApiResult<SecretReceipt>.Failure(new PluginError("push-token", PluginErrorKind.Refused, message, null) { NotSent = true });
    }
}

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>What the credential store says about a CDN source's push token.</summary>
    public enum PushTokenPresence
    {
        /// <summary>No token is stored: Push may issue one after the confirmation.</summary>
        None,

        /// <summary>A token is stored: Push uses it and issues none.</summary>
        Stored,

        /// <summary>The store could not be read: nothing is issued.</summary>
        Unreadable,
    }
}
