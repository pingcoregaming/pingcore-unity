using System;
using System.IO;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Settings;

namespace PingCore.Editor.Workspace.UI.Common
{
    /// <summary>
    /// What the PingCore window's tabs share in the Editor: the open project's root, its two
    /// settings files, the credential store the developer chose, the API endpoint
    /// (<see cref="WorkspaceEndpoint.Default"/> unless the user settings override it) and a
    /// PingCore API client over one shared <see cref="EditorHttpTransport"/>. Tests never use
    /// this class; they build the models with fakes.
    /// </summary>
    public static class WorkspaceContext
    {
        private static EditorHttpTransport transport;
        private static ICredentialStore persistentStore;
        private static ICredentialStore sessionStore;

        /// <summary>The open project's root (the folder holding <c>Assets/</c>).</summary>
        public static string ProjectRoot => Directory.GetParent(UnityEngine.Application.dataPath).FullName;

        /// <summary>
        /// The store for the developer's choice. The OS store probe runs on first use and again after
        /// <see cref="ReprobeStore"/>, which every sign-in calls, so a probe that failed once (or a store
        /// that went away) does not decide where every later key goes for the rest of the session.
        /// </summary>
        public static ICredentialStore Store(bool sessionOnly)
        {
            if (sessionOnly)
            {
                return sessionStore ?? (sessionStore = CredentialStoreSelector.Select(true));
            }

            return persistentStore ?? (persistentStore = CredentialStoreSelector.Select(false));
        }

        /// <summary>Forgets the persistent-store choice; the next <see cref="Store(bool)"/> probes the OS store again.</summary>
        public static void ReprobeStore()
        {
            persistentStore = null;
        }

        /// <summary>The store chosen in the project's user settings (the API key's).</summary>
        public static ICredentialStore Store() => Store(LoadUser(out _).SessionOnlyKey);

        /// <summary>
        /// Where push tokens live: always the persistent store, whatever the key's choice. A push token kept for this
        /// session only would be lost when the Editor quits, and the next push would issue again, replacing every other
        /// holder's copy (CI, a teammate) each session.
        /// </summary>
        public static ICredentialStore PushTokenStore() => Store(false);

        /// <summary>A client for <paramref name="endpoint"/> reading the key from <paramref name="keys"/>.</summary>
        public static IPingCoreApi Api(WorkspaceEndpoint endpoint, ICredentialStore keys)
        {
            return new PingCoreApiClient(endpoint, keys, transport ?? (transport = new EditorHttpTransport()));
        }

        /// <summary>
        /// The API endpoint of the open project: <see cref="WorkspaceEndpoint.Default"/>, or the
        /// development override in its user settings; null with a sentence when that override (or the
        /// user settings file) is refused.
        /// </summary>
        public static WorkspaceEndpoint Endpoint(out string problem)
        {
            EditorUserSettings user = LoadUser(out problem);
            if (problem != null)
            {
                return null;
            }

            return WorkspaceEndpoint.Resolve(user.ApiBaseOverride, out WorkspaceEndpoint endpoint, out problem) ? endpoint : null;
        }

        /// <summary>
        /// A client for the open project's endpoint reading the stored key, or null with a sentence
        /// saying why (a refused override, no key).
        /// </summary>
        public static IPingCoreApi SignedInApi(out string problem)
        {
            WorkspaceEndpoint endpoint = Endpoint(out problem);
            if (endpoint == null)
            {
                return null;
            }

            ICredentialStore store = Store();
            if (!HasKey(store, endpoint.Host))
            {
                problem = "Not signed in. Sign in on " + PingCoreMenu.SignInText + ".";
                return null;
            }

            return Api(endpoint, store);
        }

        /// <summary>Whether <paramref name="store"/> holds the key for <paramref name="host"/>, asked with <c>Exists</c>, never by reading it.</summary>
        public static bool HasKey(ICredentialStore store, string host)
        {
            try
            {
                return store != null && store.Exists(CredentialTargets.UserKey(host));
            }
            catch (Exception e) when (e is CredentialStoreException || e is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>The project settings; defaults and a sentence when the file is malformed.</summary>
        public static EditorProjectSettings LoadProject(out string problem)
        {
            problem = null;
            try
            {
                return EditorProjectSettings.Load(ProjectRoot);
            }
            catch (InvalidDataException e)
            {
                problem = e.Message;
                return new EditorProjectSettings();
            }
        }

        /// <summary>The user settings; defaults and a sentence when the file is malformed.</summary>
        public static EditorUserSettings LoadUser(out string problem)
        {
            problem = null;
            try
            {
                return EditorUserSettings.Load(ProjectRoot);
            }
            catch (InvalidDataException e)
            {
                problem = e.Message;
                return new EditorUserSettings();
            }
        }
    }
}
