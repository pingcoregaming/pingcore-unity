using System;

namespace PingCore.Editor.Workspace.Credentials
{
    /// <summary>Where a credential store keeps its secrets.</summary>
    public enum CredentialStoreKind
    {
        /// <summary>Windows Credential Manager (generic credentials, DPAPI-protected per user, persisted per machine).</summary>
        WindowsCredentialManager,

        /// <summary>Unity's <c>EditorPrefs</c> (the registry on Windows, a plist on macOS): outside the project, but not encrypted. Never chosen silently.</summary>
        EditorPrefs,

        /// <summary>Unity's <c>SessionState</c>: in-process memory that survives a domain reload and is gone when the Editor quits.</summary>
        Session,

        /// <summary>A test double.</summary>
        Fake,
    }

    /// <summary>A stored secret and its optional user name.</summary>
    public sealed class StoredCredential
    {
        /// <param name="secret">The secret. Never logged.</param>
        /// <param name="userName">The user name, or null.</param>
        public StoredCredential(string secret, string userName)
        {
            Secret = secret ?? throw new ArgumentNullException(nameof(secret));
            UserName = userName;
        }

        /// <summary>The secret. Never print, log or write it anywhere but the store.</summary>
        public string Secret { get; }

        /// <summary>The user name, or null.</summary>
        public string UserName { get; }

        /// <summary>Never prints the secret.</summary>
        public override string ToString() => "StoredCredential(secret withheld)";
    }

    /// <summary>
    /// The seam for every plugin credential (the <c>usr_</c> key and
    /// <c>cdnpush_</c> tokens). Targets come from <see cref="CredentialTargets"/>. A store never logs a secret
    /// and never writes one into the project. Failures of the store itself throw
    /// <see cref="CredentialStoreException"/>, whose message never holds a secret.
    /// </summary>
    public interface ICredentialStore
    {
        /// <summary>Where this store keeps secrets.</summary>
        CredentialStoreKind Kind { get; }

        /// <summary>One sentence for the settings page, for example why the fallback was chosen.</summary>
        string Description { get; }

        /// <summary>Reads a credential; null when none is stored under <paramref name="target"/>.</summary>
        StoredCredential Read(string target);

        /// <summary>True when a credential is stored under <paramref name="target"/>. Never reads the secret into managed memory where the store can avoid it.</summary>
        bool Exists(string target);

        /// <summary>Stores a credential, replacing any under the same target.</summary>
        void Write(string target, string secret, string userName = null);

        /// <summary>Deletes a credential; false when none was stored.</summary>
        bool Delete(string target);
    }

    /// <summary>The credential store itself failed. The message names the operation and the error code, never a secret.</summary>
    public sealed class CredentialStoreException : Exception
    {
        /// <param name="message">A message without any secret.</param>
        public CredentialStoreException(string message)
            : base(message)
        {
        }
    }
}
