using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Confirmation
{
    /// <summary>
    /// The build guard's confirmation writer: "Confirm for build" on Window > PingCore, Player hosting.
    /// In order, and refusing at the first failure (<see cref="HeartbeatTokenConfirmationPolicy"/>):
    /// <list type="number">
    /// <item>local checks: the token is <c>dsc_</c>-shaped, the app id is a <c>dscp_</c> public id,
    /// and the token is none of the plugin's stored credentials;</item>
    /// <item><c>GET discovery/apps</c>, the app whose <c>publicId</c> matches (none: refused);</item>
    /// <item><c>GET discovery/apps/{id}</c>: open and enabled, exactly one active heartbeat token
    /// ending in the token's last four characters, and no active token of another scope ending in
    /// them;</item>
    /// <item>the record <c>{appPublicId, tokenDigest (SHA-256), scope: heartbeat,
    /// registrationMode: open, confirmedAt}</c> written through
    /// <see cref="BuildGuardConfirmationFile.Write"/>, replacing an older record for the same app
    /// and keeping every other app's.</item>
    /// </list>
    /// The token is never logged, stored or sent; only its digest is written. See
    /// <see cref="HeartbeatTokenConfirmationPolicy.LastFourLimitation"/> for what the last-four
    /// match can and cannot prove.
    /// </summary>
    public sealed class HeartbeatTokenConfirmationWriter
    {
        private readonly IPingCoreApi api;
        private readonly Func<DateTime> utcNow;

        /// <param name="api">The signed-in workspace (<c>discovery.view</c> needed).</param>
        /// <param name="utcNow">The clock for <c>confirmedAt</c>; <see cref="DateTime.UtcNow"/> when null.</param>
        public HeartbeatTokenConfirmationWriter(IPingCoreApi api, Func<DateTime> utcNow = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// The secrets of the plugin's own credentials for <paramref name="host"/> that the token must
        /// not equal: the <c>usr_</c> key and, when a CDN source is configured, its push token. A
        /// store that cannot be read yields what it could read; the shapes of those credentials
        /// (<c>usr_</c>, <c>cdnpush_</c>) can never pass the <c>dsc_</c> check anyway.
        /// </summary>
        public static IReadOnlyList<string> StoredSecrets(ICredentialStore store, string host, long cdnSourceId)
        {
            var secrets = new List<string>();
            if (store == null || !CredentialTargets.IsValidHost(host))
            {
                return secrets;
            }

            var targets = new List<string> { CredentialTargets.UserKey(host) };
            if (cdnSourceId > 0)
            {
                targets.Add(CredentialTargets.PushToken(host, cdnSourceId));
            }

            foreach (string target in targets)
            {
                try
                {
                    StoredCredential stored = store.Read(target);
                    if (stored != null)
                    {
                        secrets.Add(stored.Secret);
                    }
                }
                catch (CredentialStoreException)
                {
                    // Unreadable: nothing to compare against for this target.
                }
            }

            return secrets;
        }

        /// <summary>Runs the checks and, when they pass, writes the record under <paramref name="projectRoot"/>.</summary>
        /// <param name="projectRoot">The Unity project root (its <c>ProjectSettings/</c> receives the record).</param>
        /// <param name="token">The configured <c>openRegistrationHeartbeatToken</c>, trimmed.</param>
        /// <param name="appPublicId">The open app's <c>dscp_</c> public id (the settings' community app id).</param>
        /// <param name="storedSecrets">The plugin's own credentials (<see cref="StoredSecrets"/>).</param>
        public async Task<ConfirmationResult> ConfirmAsync(string projectRoot, string token, string appPublicId, IEnumerable<string> storedSecrets, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new ArgumentException("A project root is required.", nameof(projectRoot));
            }

            token = token?.Trim();
            appPublicId = appPublicId?.Trim();
            ConfirmationResult local = HeartbeatTokenConfirmationPolicy.LocalCheck(token, appPublicId, storedSecrets);
            if (local != null)
            {
                return local;
            }

            ApiResult<DiscoveryAppListResponse> list = await api.ListDiscoveryAppsAsync(cancellationToken);
            if (!list.Ok)
            {
                return ConfirmationResult.Failed(list.Error);
            }

            DiscoveryAppListItem found = HeartbeatTokenConfirmationPolicy.FindApp(list.Value, appPublicId);
            if (found == null)
            {
                return ConfirmationResult.Refused(ConfirmationOutcome.AppNotFound,
                    "No Discovery app with that public id exists in this workspace.",
                    "Check the community app public id, and that you are signed in to the workspace that owns it.");
            }

            ApiResult<DiscoveryAppDetailResponse> detail = await api.GetDiscoveryAppAsync(found.DiscoveryAppId, cancellationToken);
            if (!detail.Ok)
            {
                return ConfirmationResult.Failed(detail.Error);
            }

            ConfirmationResult decided = HeartbeatTokenConfirmationPolicy.Decide(detail.Value, appPublicId, token);
            if (decided != null)
            {
                return decided;
            }

            BuildGuardConfirmation confirmation = BuildGuardConfirmations.Create(token, appPublicId, utcNow());
            List<BuildGuardConfirmation> kept = BuildGuardConfirmationFile.Read(projectRoot)
                .Where(c => c != null && !string.Equals(c.appPublicId, appPublicId, StringComparison.Ordinal))
                .ToList();
            kept.Add(confirmation);
            BuildGuardConfirmationFile.Write(projectRoot, new BuildGuardConfirmationRecord { confirmations = kept.ToArray() });
            return ConfirmationResult.Confirmed(confirmation, detail.Value.App.Name);
        }
    }
}
