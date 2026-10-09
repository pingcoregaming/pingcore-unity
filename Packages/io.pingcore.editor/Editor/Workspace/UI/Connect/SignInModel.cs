using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;

namespace PingCore.Editor.Workspace.UI.Connect
{
    /// <summary>
    /// The logic behind Connect's sign-in and Ship's own-pingctl path, without UI so it can be tested with
    /// fakes. There is no workspace URL: the endpoint is <see cref="WorkspaceEndpoint.Default"/>
    /// unless <c>UserSettings/PingCoreEditorUser.json</c> overrides it, and the API resolves the
    /// workspace from the key. The key travels from the password field to
    /// <see cref="SignInService"/> and from there only into the credential store; this model keeps
    /// no copy, and what it writes to the project is the developer's pingctl path, push folder and session-only
    /// choice (<c>UserSettings/PingCoreEditorUser.json</c>), never the key. Sign-out deletes the
    /// key from both stores (the OS store or its fallback, and this session's).
    /// </summary>
    public sealed class SignInModel
    {
        private readonly string projectRoot;
        private readonly Func<bool, ICredentialStore> storeFor;
        private readonly Func<WorkspaceEndpoint, ICredentialStore, IPingCoreApi> apiFactory;
        private readonly Action reprobeStore;

        /// <param name="projectRoot">The Unity project whose settings files are read and written.</param>
        /// <param name="storeFor">The credential store for the session-only choice.</param>
        /// <param name="apiFactory">Builds an API client for an endpoint and a key store.</param>
        /// <param name="reprobeStore">Forgets the persistent-store choice so the next use probes the OS store again; run before every sign-in.</param>
        public SignInModel(string projectRoot, Func<bool, ICredentialStore> storeFor, Func<WorkspaceEndpoint, ICredentialStore, IPingCoreApi> apiFactory, Action reprobeStore = null)
        {
            this.projectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
            this.storeFor = storeFor ?? throw new ArgumentNullException(nameof(storeFor));
            this.apiFactory = apiFactory ?? throw new ArgumentNullException(nameof(apiFactory));
            this.reprobeStore = reprobeStore;
            Reload();
        }

        /// <summary>The shared project settings.</summary>
        public EditorProjectSettings Project { get; private set; }

        /// <summary>The developer's own settings.</summary>
        public EditorUserSettings User { get; private set; }

        /// <summary>A sentence when a settings file could not be read, else null.</summary>
        public string LoadProblem { get; private set; }

        /// <summary>The endpoint every call goes to, or null when the override is refused (<see cref="EndpointProblem"/>).</summary>
        public WorkspaceEndpoint Endpoint { get; private set; }

        /// <summary>Why <see cref="Endpoint"/> is null, else null.</summary>
        public string EndpointProblem { get; private set; }

        /// <summary>The last sign-in or verification, or null.</summary>
        public SignInResult LastResult { get; private set; }

        /// <summary>The store in use for the current session-only choice.</summary>
        public ICredentialStore Store => storeFor(User.SessionOnlyKey);

        /// <summary>A sentence when a development override is in use (the UI shows it as a warning), else null.</summary>
        public string OverrideNote => Endpoint != null && Endpoint.IsOverride
            ? $"Development override: every call goes to {Endpoint.ApiBase} (apiBaseOverride in {EditorUserSettings.RelativePath}), not {WorkspaceEndpoint.DefaultApiBase}."
            : null;

        /// <summary>Re-reads both settings files and the endpoint.</summary>
        public void Reload()
        {
            LoadProblem = null;
            try
            {
                Project = EditorProjectSettings.Load(projectRoot);
            }
            catch (InvalidDataException e)
            {
                Project = new EditorProjectSettings();
                LoadProblem = e.Message;
            }

            try
            {
                User = EditorUserSettings.Load(projectRoot);
            }
            catch (InvalidDataException e)
            {
                User = new EditorUserSettings();
                LoadProblem = (LoadProblem == null ? string.Empty : LoadProblem + " ") + e.Message;
            }

            Endpoint = WorkspaceEndpoint.Resolve(User.ApiBaseOverride, out WorkspaceEndpoint endpoint, out string problem) ? endpoint : null;
            EndpointProblem = problem;
        }

        /// <summary>The banner over the key field: what store holds the key, and whether that is a warning.</summary>
        public (string Text, bool Warning) StoreBanner()
        {
            ICredentialStore store = Store;
            switch (store.Kind)
            {
                case CredentialStoreKind.EditorPrefs:
                    return (store.Description, true);
                case CredentialStoreKind.Session:
                    return ("This session only: the key is kept in memory and is gone when the Editor quits.", false);
                case CredentialStoreKind.WindowsCredentialManager:
                    return ("The key is kept in Windows Credential Manager, never in the project.", false);
                default:
                    return (store.Description, false);
            }
        }

        /// <summary>
        /// Whether a key is stored for the endpoint, asked with <see cref="ICredentialStore.Exists"/>, so the
        /// secret is never read just to answer it (the window asks on every refresh).
        /// </summary>
        public bool HasStoredKey()
        {
            if (Endpoint == null)
            {
                return false;
            }

            try
            {
                return Store.Exists(CredentialTargets.UserKey(Endpoint.Host));
            }
            catch (Exception e) when (e is CredentialStoreException || e is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// True when the workspace refused the key itself on the last sign-in or verification (an HTTP 401); a
        /// credential store that could not be read, or a call never sent, is not a refusal.
        /// </summary>
        public bool KeyRefused => LastResult != null && !LastResult.Ok && LastResult.Error?.Kind == PluginErrorKind.NotSignedIn && LastResult.Error.HttpStatus == 401;

        /// <summary><c>usr_...abcd</c> for the stored key, or null.</summary>
        public string StoredKeyMask()
        {
            return Endpoint == null ? null : new SignInService(Store, apiFactory).StoredKeyMask(Endpoint.Host);
        }

        /// <summary>
        /// Verifies <paramref name="key"/>, reads the workspace's name and stores the key. The caller
        /// clears the key field whatever the outcome.
        /// </summary>
        public async Task<SignInResult> SignInAsync(string key, CancellationToken cancellationToken)
        {
            if (Endpoint == null)
            {
                LastResult = SignInResult.Failed(null, new PluginError("verify-key", PluginErrorKind.Refused, EndpointProblem, null));
                return LastResult;
            }

            // The OS store may have become usable (or stopped being) since the last probe: ask again before storing a key.
            reprobeStore?.Invoke();
            LastResult = await new SignInService(Store, apiFactory).SignInAsync(Endpoint, key, cancellationToken);
            return LastResult;
        }

        /// <summary>Verifies the stored key and reads the workspace's name again.</summary>
        public async Task<SignInResult> VerifyAsync(CancellationToken cancellationToken)
        {
            if (Endpoint == null)
            {
                LastResult = SignInResult.Failed(null, new PluginError("verify-key", PluginErrorKind.Refused, EndpointProblem, null));
                return LastResult;
            }

            LastResult = await new SignInService(Store, apiFactory).VerifyAsync(Endpoint, cancellationToken);
            return LastResult;
        }

        /// <summary>Deletes the stored key from the persistent store and this session's.</summary>
        public string SignOut()
        {
            if (Endpoint == null)
            {
                return EndpointProblem;
            }

            LastResult = null;
            bool deleted = DeleteEverywhere(Endpoint.Host, out string problem);
            if (problem != null)
            {
                return problem;
            }

            return deleted ? "Signed out; the key was deleted from the credential store." : "No key was stored.";
        }

        // Deletes host's key from the persistent store AND this session's, whichever the choice is now: a key stored under
        // the other choice must not outlive a sign-out. True when either held one; problem names a store that failed.
        private bool DeleteEverywhere(string host, out string problem)
        {
            problem = null;
            bool deleted = false;
            foreach (bool sessionOnly in new[] { false, true })
            {
                try
                {
                    deleted |= new SignInService(storeFor(sessionOnly), apiFactory).SignOut(host);
                }
                catch (CredentialStoreException e)
                {
                    problem = "The key could not be deleted: " + e.Message;
                }
            }

            return deleted;
        }

        /// <summary>Switches between the OS store and "this session only". A key already stored stays where it is.</summary>
        public void SetSessionOnly(bool sessionOnly)
        {
            if (User.SessionOnlyKey == sessionOnly)
            {
                return;
            }

            User.SessionOnlyKey = sessionOnly;
            User.Save(projectRoot);
        }

        /// <summary>Stores the developer's own pingctl (empty for the bundled one). Refuses a value that looks like a credential.</summary>
        public void SetPingctlPath(string path)
        {
            User.PingctlPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            User.Save(projectRoot);
        }

        /// <summary>Stores the folder Push sends (empty for none). Refuses a value that looks like a credential.</summary>
        public void SetPushFolder(string folder)
        {
            User.PushFolder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
            User.Save(projectRoot);
        }

        /// <summary>
        /// Connect's signed-in line, "Signed in to &lt;workspace&gt; as &lt;email&gt;." (the masked key when the
        /// workspace answered no email); null for a failed or missing result.
        /// </summary>
        public static string SignedInLine(SignInResult result)
        {
            if (result == null || !result.Ok)
            {
                return null;
            }

            string email = result.Identity?.Identity?.Email;
            string who = string.IsNullOrWhiteSpace(email) ? result.MaskedKey : email.Trim();
            return result.WorkspaceName != null
                ? $"Signed in to {result.WorkspaceName} as {who}."
                : $"Signed in as {who} (the workspace's name could not be read).";
        }

        /// <summary>The verification summary: the workspace's name, the masked key, the brand member's role and the fleets the key can see.</summary>
        public static string Describe(SignInResult result)
        {
            if (result == null)
            {
                return "Not verified yet.";
            }

            if (!result.Ok)
            {
                return ErrorText.Of(result.Error);
            }

            string who = result.WorkspaceName != null
                ? $"Signed in to {result.WorkspaceName} as {result.MaskedKey}"
                : $"Signed in as {result.MaskedKey} (the workspace's name could not be read{(result.IdentityError == null ? string.Empty : ": " + ErrorText.Of(result.IdentityError))})";
            CapabilitiesIdentity identity = result.Identity?.Identity;
            string role = identity == null ? string.Empty : identity.IsOwner ? ", the workspace owner" : identity.IsBrandMember ? ", a brand member" : ", not a brand member of any workspace";
            IReadOnlyList<FleetView> fleets = result.Fleets;
            string list = fleets.Count == 0
                ? "none (the key can see no fleet, or the workspace has none)"
                : string.Join(", ", fleets.Select(f => $"{f.Name} (#{f.FleetId}, {f.Status})"));
            return $"{who}{role}. Fleets: {list}.";
        }
    }
}
