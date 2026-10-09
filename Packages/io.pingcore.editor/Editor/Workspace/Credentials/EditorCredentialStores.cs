using System;
using UnityEditor;

namespace PingCore.Editor.Workspace.Credentials
{
    /// <summary>
    /// The fallback store: Unity's <c>EditorPrefs</c> (the Windows registry or a macOS plist),
    /// outside the project but not encrypted. Keys are <c>PingCore.&lt;host&gt;.usr</c> and the
    /// like (<see cref="CredentialTargets.EditorPrefsKey"/>), with the user name under the same key
    /// plus <c>.user</c>. Never chosen silently: <see cref="Description"/> says why it is in use and
    /// the settings page shows it as a persistent warning.
    /// </summary>
    public sealed class EditorPrefsCredentialStore : ICredentialStore
    {
        /// <param name="reason">Why the OS store is not in use, shown to the developer.</param>
        public EditorPrefsCredentialStore(string reason)
        {
            Reason = string.IsNullOrEmpty(reason) ? "The OS credential store is not available." : reason;
        }

        /// <summary>Why the fallback was chosen.</summary>
        public string Reason { get; }

        public CredentialStoreKind Kind => CredentialStoreKind.EditorPrefs;

        public string Description => "Stored in EditorPrefs, not the OS credential store: " + Reason;

        public StoredCredential Read(string target)
        {
            string key = CredentialTargets.EditorPrefsKey(target);
            string secret = EditorPrefs.GetString(key, null);
            if (string.IsNullOrEmpty(secret))
            {
                return null;
            }

            string user = EditorPrefs.GetString(key + ".user", null);
            return new StoredCredential(secret, string.IsNullOrEmpty(user) ? null : user);
        }

        public bool Exists(string target) => EditorPrefs.HasKey(CredentialTargets.EditorPrefsKey(target));

        public void Write(string target, string secret, string userName = null)
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("An empty secret is never stored.", nameof(secret));
            }

            string key = CredentialTargets.EditorPrefsKey(target);
            EditorPrefs.SetString(key, secret);
            if (userName == null)
            {
                EditorPrefs.DeleteKey(key + ".user");
            }
            else
            {
                EditorPrefs.SetString(key + ".user", userName);
            }
        }

        public bool Delete(string target)
        {
            string key = CredentialTargets.EditorPrefsKey(target);
            bool had = EditorPrefs.HasKey(key);
            EditorPrefs.DeleteKey(key);
            EditorPrefs.DeleteKey(key + ".user");
            return had;
        }
    }

    /// <summary>
    /// "This session only": Unity's <c>SessionState</c>, in-process memory that survives a domain
    /// reload and is gone when the Editor quits. Nothing reaches the disk.
    /// </summary>
    public sealed class SessionCredentialStore : ICredentialStore
    {
        public CredentialStoreKind Kind => CredentialStoreKind.Session;

        public string Description => "Kept for this Editor session only; gone when the Editor quits.";

        public StoredCredential Read(string target)
        {
            string key = CredentialTargets.EditorPrefsKey(target);
            string secret = SessionState.GetString(key, null);
            if (string.IsNullOrEmpty(secret))
            {
                return null;
            }

            string user = SessionState.GetString(key + ".user", null);
            return new StoredCredential(secret, string.IsNullOrEmpty(user) ? null : user);
        }

        // SessionState has no key test; an empty string is what an absent key reads as.
        public bool Exists(string target) => !string.IsNullOrEmpty(SessionState.GetString(CredentialTargets.EditorPrefsKey(target), null));

        public void Write(string target, string secret, string userName = null)
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("An empty secret is never stored.", nameof(secret));
            }

            string key = CredentialTargets.EditorPrefsKey(target);
            SessionState.SetString(key, secret);
            if (userName == null)
            {
                SessionState.EraseString(key + ".user");
            }
            else
            {
                SessionState.SetString(key + ".user", userName);
            }
        }

        public bool Delete(string target)
        {
            string key = CredentialTargets.EditorPrefsKey(target);
            bool had = !string.IsNullOrEmpty(SessionState.GetString(key, null));
            SessionState.EraseString(key);
            SessionState.EraseString(key + ".user");
            return had;
        }
    }

    /// <summary>
    /// Chooses the credential store. On Windows it is Windows Credential Manager, unless its probe
    /// fails, and then the <c>EditorPrefs</c> fallback with the probe's reason. On macOS and Linux
    /// this version has no OS store implementation (the Keychain through <c>/usr/bin/security</c>
    /// and libsecret through <c>secret-tool</c>, both with the secret on standard input, are the
    /// planned seams behind <see cref="ICredentialStore"/>), so they get the fallback with a reason
    /// that says so. "This session only" always gets <see cref="SessionCredentialStore"/>.
    /// </summary>
    public static class CredentialStoreSelector
    {
        /// <summary>The store to use.</summary>
        /// <param name="sessionOnly">The developer's "this session only" choice.</param>
        public static ICredentialStore Select(bool sessionOnly)
        {
            if (sessionOnly)
            {
                return new SessionCredentialStore();
            }

            return SelectPersistent(WindowsCredentialStore.IsSupportedPlatform, WindowsCredentialStore.Probe, Platform());
        }

        /// <summary>The decision, with its inputs injected so the table is testable.</summary>
        internal static ICredentialStore SelectPersistent(bool onWindows, Func<string> probeWindows, string platformName)
        {
            if (onWindows)
            {
                string problem = probeWindows();
                return problem == null ? (ICredentialStore)new WindowsCredentialStore() : new EditorPrefsCredentialStore(problem);
            }

            return new EditorPrefsCredentialStore($"this version of the plugin has no {platformName} credential store support yet.");
        }

        private static string Platform()
        {
            switch (UnityEngine.Application.platform)
            {
                case UnityEngine.RuntimePlatform.OSXEditor:
                    return "macOS Keychain";
                case UnityEngine.RuntimePlatform.LinuxEditor:
                    return "Linux libsecret";
                default:
                    return "OS";
            }
        }
    }
}
