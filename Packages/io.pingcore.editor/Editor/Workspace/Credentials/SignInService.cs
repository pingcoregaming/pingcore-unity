using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Credentials
{
    /// <summary>
    /// Sign-in, verification and sign-out. There is no workspace URL: the client calls one API base
    /// (<see cref="WorkspaceEndpoint.Default"/>, or a development override) and the API resolves the
    /// workspace from the key. Sign-in checks the key's shape, verifies it with <c>GET fleets</c>
    /// through a client that holds it in memory for those calls, reads the workspace's name with
    /// <c>GET me/capabilities</c> (a failure there leaves the name unknown, never the sign-in), and
    /// only then writes the key to the credential store; a key the API refuses is never stored.
    /// Sign-out deletes the target. The key is shown only as <see cref="Mask"/> (<c>usr_...abcd</c>).
    /// </summary>
    public sealed class SignInService
    {
        private static readonly Regex KeyShape = new Regex("^usr_[A-Za-z0-9]{16,}$", RegexOptions.CultureInvariant);

        private readonly ICredentialStore store;
        private readonly Func<WorkspaceEndpoint, ICredentialStore, IPingCoreApi> apiFactory;

        /// <param name="store">Where the key is kept (<see cref="CredentialStoreSelector.Select"/>).</param>
        /// <param name="apiFactory">Builds a client for an endpoint and a key store; the Editor passes
        /// <c>(endpoint, keys) =&gt; new PingCoreApiClient(endpoint, keys, transport)</c>, tests a fake.</param>
        public SignInService(ICredentialStore store, Func<WorkspaceEndpoint, ICredentialStore, IPingCoreApi> apiFactory)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.apiFactory = apiFactory ?? throw new ArgumentNullException(nameof(apiFactory));
        }

        /// <summary>The store this service writes to.</summary>
        public ICredentialStore Store => store;

        /// <summary><c>usr_...</c> plus the key's last four characters; a short or malformed key shows its prefix only.</summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            int underscore = key.IndexOf('_');
            string prefix = underscore > 0 && underscore <= 8 ? key.Substring(0, underscore + 1) : string.Empty;
            return key.Length >= prefix.Length + 12 ? prefix + "..." + key.Substring(key.Length - 4) : prefix + "...";
        }

        /// <summary>True for a <c>usr_</c> key shape (the prefix and 16 or more letters or digits).</summary>
        public static bool IsKeyShaped(string key) => key != null && KeyShape.IsMatch(key);

        /// <summary>Verifies <paramref name="key"/> against <paramref name="endpoint"/> and stores it on success.</summary>
        public async Task<SignInResult> SignInAsync(WorkspaceEndpoint endpoint, string key, CancellationToken cancellationToken)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            key = key?.Trim();
            if (!IsKeyShaped(key))
            {
                return SignInResult.Failed(endpoint.Host, new PluginError("verify-key", PluginErrorKind.Refused,
                    "That is not a usr_ API key (usr_ followed by letters and digits).", "Create a key for a brand member in your workspace under API keys."));
            }

            var oneCall = new OneKeyStore(CredentialTargets.UserKey(endpoint.Host), key);
            IPingCoreApi api = apiFactory(endpoint, oneCall);
            ApiResult<FleetListResponse> verified = await api.ListFleetsAsync(cancellationToken);
            if (!verified.Ok)
            {
                return SignInResult.Failed(endpoint.Host, verified.Error);
            }

            ApiResult<CapabilitiesResponse> identity = await api.GetCapabilitiesAsync(cancellationToken);
            try
            {
                store.Write(CredentialTargets.UserKey(endpoint.Host), key);
            }
            catch (CredentialStoreException e)
            {
                return SignInResult.Failed(endpoint.Host, new PluginError("verify-key", PluginErrorKind.Refused,
                    "The key is valid but could not be stored: " + new Redactor(new[] { key }).Redact(e.Message), "Try again, or keep it for this session only."));
            }

            return SignInResult.SignedIn(endpoint.Host, Mask(key), store.Kind, store.Description, verified.Value.Fleets ?? new List<FleetView>(), identity);
        }

        /// <summary>Verifies the stored key for <paramref name="endpoint"/> (Connect's Verify button).</summary>
        public async Task<SignInResult> VerifyAsync(WorkspaceEndpoint endpoint, CancellationToken cancellationToken)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            string mask;
            try
            {
                StoredCredential stored = store.Read(CredentialTargets.UserKey(endpoint.Host));
                if (stored == null)
                {
                    return SignInResult.Failed(endpoint.Host, new PluginError("verify-key", PluginErrorKind.NotSignedIn, "No key is stored.", "Sign in on " + PingCoreMenu.SignInText + "."));
                }

                mask = Mask(stored.Secret);
            }
            catch (CredentialStoreException e)
            {
                return SignInResult.Failed(endpoint.Host, new PluginError("verify-key", PluginErrorKind.NotSignedIn, "The credential store could not be read: " + Redactor.PatternsOnly.Redact(e.Message), null));
            }

            IPingCoreApi api = apiFactory(endpoint, store);
            ApiResult<FleetListResponse> verified = await api.ListFleetsAsync(cancellationToken);
            if (!verified.Ok)
            {
                return SignInResult.Failed(endpoint.Host, verified.Error);
            }

            ApiResult<CapabilitiesResponse> identity = await api.GetCapabilitiesAsync(cancellationToken);
            return SignInResult.SignedIn(endpoint.Host, mask, store.Kind, store.Description, verified.Value.Fleets ?? new List<FleetView>(), identity);
        }

        /// <summary>The masked stored key for <paramref name="host"/>, or null when none is stored or the store failed.</summary>
        public string StoredKeyMask(string host)
        {
            try
            {
                StoredCredential stored = store.Read(CredentialTargets.UserKey(host));
                return stored == null ? null : Mask(stored.Secret);
            }
            catch (Exception e) when (e is CredentialStoreException || e is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Deletes the stored key for <paramref name="host"/>. False when none was stored.</summary>
        public bool SignOut(string host)
        {
            return store.Delete(CredentialTargets.UserKey(host));
        }

        /// <summary>A store that holds one key in memory for the verification call and refuses writes.</summary>
        private sealed class OneKeyStore : ICredentialStore
        {
            private readonly string target;
            private readonly string key;

            public OneKeyStore(string target, string key)
            {
                this.target = target;
                this.key = key;
            }

            public CredentialStoreKind Kind => CredentialStoreKind.Session;

            public string Description => "verification only";

            public StoredCredential Read(string name) => name == target ? new StoredCredential(key, null) : null;

            public bool Exists(string name) => name == target;

            public void Write(string name, string secret, string userName = null) => throw new CredentialStoreException("The verification store is read-only.");

            public bool Delete(string name) => false;
        }
    }

    /// <summary>The outcome of a sign-in or verification.</summary>
    public sealed class SignInResult
    {
        private SignInResult(string host, string maskedKey, CredentialStoreKind storeKind, string storeDescription, IReadOnlyList<FleetView> fleets, CapabilitiesResponse identity, PluginError identityError, PluginError error)
        {
            Host = host;
            MaskedKey = maskedKey;
            StoreKind = storeKind;
            StoreDescription = storeDescription;
            Fleets = fleets ?? Array.Empty<FleetView>();
            Identity = identity;
            IdentityError = identityError;
            Error = error;
        }

        public bool Ok => Error == null;

        /// <summary>The API host the key belongs to (<c>app.pingcore.io</c> unless overridden).</summary>
        public string Host { get; }

        /// <summary><c>usr_...abcd</c>, or null on failure.</summary>
        public string MaskedKey { get; }

        public CredentialStoreKind StoreKind { get; }

        /// <summary>The store's sentence (the fallback's warning when it is <c>EditorPrefs</c>).</summary>
        public string StoreDescription { get; }

        /// <summary>The fleets the key can see.</summary>
        public IReadOnlyList<FleetView> Fleets { get; }

        /// <summary>Who the key acts for (<c>GET me/capabilities</c>), or null when that read failed.</summary>
        public CapabilitiesResponse Identity { get; }

        /// <summary>Why <see cref="Identity"/> is null after a successful sign-in, or null.</summary>
        public PluginError IdentityError { get; }

        /// <summary>The workspace's name, or null when the API did not say.</summary>
        public string WorkspaceName => string.IsNullOrWhiteSpace(Identity?.Identity?.BrandName) ? null : Identity.Identity.BrandName.Trim();

        /// <summary>The brand permissions the key's brand member holds, or null when unknown.</summary>
        public IReadOnlyList<string> Permissions => Identity?.Permissions;

        public PluginError Error { get; }

        internal static SignInResult SignedIn(string host, string mask, CredentialStoreKind kind, string description, IReadOnlyList<FleetView> fleets, ApiResult<CapabilitiesResponse> identity)
            => new SignInResult(host, mask, kind, description, fleets, identity != null && identity.Ok ? identity.Value : null, identity != null && !identity.Ok ? identity.Error : null, null);

        internal static SignInResult Failed(string host, PluginError error)
            => new SignInResult(host, null, default, null, null, null, null, error);
    }
}
