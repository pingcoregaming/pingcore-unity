using System;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Infrastructure
{
    /// <summary>
    /// A credential store whose every call runs on the Editor's main thread (<see cref="MainThreadCalls"/>), for the
    /// missing-infrastructure answer, which runs on a thread-pool thread. Only the stores built on main-thread-only Unity
    /// APIs are wrapped: <c>EditorPrefs</c> and <c>SessionState</c>. Windows Credential Manager is plain P/Invoke and is
    /// used as it is. A call the main thread does not run within the bound fails as a <see cref="CredentialStoreException"/>,
    /// which every caller already treats as "not signed in".
    /// </summary>
    internal sealed class MainThreadCredentialStore : ICredentialStore
    {
        private readonly ICredentialStore inner;
        private readonly MainThreadCalls mainThread;
        private readonly TimeSpan bound;

        private MainThreadCredentialStore(ICredentialStore inner, MainThreadCalls mainThread, TimeSpan bound)
        {
            this.inner = inner;
            this.mainThread = mainThread;
            this.bound = bound;
        }

        public CredentialStoreKind Kind => inner.Kind;

        public string Description => inner.Description;

        /// <summary><paramref name="store"/> itself when it is safe off the main thread (or null); else the wrapper.</summary>
        internal static ICredentialStore Wrap(ICredentialStore store, MainThreadCalls mainThread, TimeSpan bound)
        {
            if (mainThread == null)
            {
                throw new ArgumentNullException(nameof(mainThread));
            }

            bool unityBound = store != null && (store.Kind == CredentialStoreKind.EditorPrefs || store.Kind == CredentialStoreKind.Session);
            return unityBound ? new MainThreadCredentialStore(store, mainThread, bound) : store;
        }

        public StoredCredential Read(string target) => OnMainThread(() => inner.Read(target));

        public bool Exists(string target) => OnMainThread(() => inner.Exists(target));

        public void Write(string target, string secret, string userName = null) => OnMainThread(() =>
        {
            inner.Write(target, secret, userName);
            return true;
        });

        public bool Delete(string target) => OnMainThread(() => inner.Delete(target));

        private T OnMainThread<T>(Func<T> call)
        {
            try
            {
                return mainThread.Run(call, bound);
            }
            catch (TimeoutException)
            {
                throw new CredentialStoreException($"The credential store ({inner.Kind}) did not answer on the Editor's main thread within {bound.TotalSeconds:0.#} s.");
            }
        }
    }
}
