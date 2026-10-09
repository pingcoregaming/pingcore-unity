using System;
using NUnit.Framework;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Tests.Credentials
{
    /// <summary>
    /// The credential targets, the store choice, and a real Windows Credential Manager round trip
    /// under a test-only target that teardown deletes.
    /// </summary>
    public sealed class CredentialStoreTests
    {
        private const string TestHost = "w1-tests.pingcore.invalid";
        private string target;

        [SetUp]
        public void SetUp() => target = CredentialTargets.TestTarget(TestHost, Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (WindowsCredentialStore.IsSupportedPlatform)
            {
                try
                {
                    new WindowsCredentialStore().Delete(target);
                }
                catch (CredentialStoreException)
                {
                    // Nothing to clean.
                }
            }
        }

        [Test]
        public void TargetsAreNamespacedPerWorkspaceHost()
        {
            Assert.That(CredentialTargets.UserKey("studio.app.pingcore.io"), Is.EqualTo("PingCore/studio.app.pingcore.io/usr"));
            Assert.That(CredentialTargets.PushToken("studio.app.pingcore.io", 31), Is.EqualTo("PingCore/studio.app.pingcore.io/cdnpush/31"));
            Assert.That(CredentialTargets.IsTarget("PingCore/studio.app.pingcore.io/registry/5"), Is.False, "the plugin keeps no registry push credential");
            Assert.That(CredentialTargets.IsTarget("PingCore/studio.app.pingcore.io/dsc/7/backend-read"), Is.False, "nor a Discovery token");
            Assert.That(CredentialTargets.EditorPrefsKey(CredentialTargets.UserKey("studio.app.pingcore.io")), Is.EqualTo("PingCore.studio.app.pingcore.io.usr"));
            Assert.Throws<ArgumentException>(() => CredentialTargets.UserKey("Studio.App"), "upper case");
            Assert.Throws<ArgumentException>(() => CredentialTargets.UserKey("evil/host"));
            Assert.Throws<ArgumentOutOfRangeException>(() => CredentialTargets.PushToken("studio.app.pingcore.io", 0));
            Assert.That(CredentialTargets.IsTarget("PingCore/studio.app.pingcore.io/usr/extra"), Is.False);
            Assert.That(CredentialTargets.IsTarget("Other/studio.app.pingcore.io/usr"), Is.False);
            Assert.That(CredentialTargets.IsTarget(target), Is.True);
        }

        [Test]
        public void TheSelectorUsesTheWindowsStoreWhenItsProbeAnswersAndNamesTheReasonOtherwise()
        {
            Assert.That(CredentialStoreSelector.SelectPersistent(true, () => null, "OS").Kind, Is.EqualTo(CredentialStoreKind.WindowsCredentialManager));

            ICredentialStore fallback = CredentialStoreSelector.SelectPersistent(true, () => "Windows Credential Manager did not answer (Windows error 1312).", "OS");
            Assert.That(fallback.Kind, Is.EqualTo(CredentialStoreKind.EditorPrefs));
            Assert.That(fallback.Description, Does.Contain("not the OS credential store").And.Contain("1312"));

            ICredentialStore mac = CredentialStoreSelector.SelectPersistent(false, () => throw new AssertionException("never probed off Windows"), "macOS Keychain");
            Assert.That(mac.Kind, Is.EqualTo(CredentialStoreKind.EditorPrefs));
            Assert.That(mac.Description, Does.Contain("macOS Keychain"));

            Assert.That(CredentialStoreSelector.Select(sessionOnly: true).Kind, Is.EqualTo(CredentialStoreKind.Session));
        }

        [Test]
        public void TheWindowsStoreRoundTripsASecretAndAUserNameAndDeletes()
        {
            if (!WindowsCredentialStore.IsSupportedPlatform)
            {
                Assert.Ignore("Windows Credential Manager exists only on Windows; macOS and Linux use the EditorPrefs fallback in this version.");
            }

            Assert.That(WindowsCredentialStore.Probe(), Is.Null, "the store answers on this machine");
            var store = new WindowsCredentialStore();
            string secret = "usr_" + Guid.NewGuid().ToString("N");
            Assert.That(store.Read(target), Is.Null, "precondition: a fresh target is empty");
            Assert.That(store.Exists(target), Is.False, "nothing stored yet");

            store.Write(target, secret);
            Assert.That(store.Exists(target), Is.True, "Exists sees the written credential");
            StoredCredential read = store.Read(target);
            Assert.That(read.Secret, Is.EqualTo(secret));
            Assert.That(read.UserName, Is.Null);
            Assert.That(read.ToString(), Does.Not.Contain(secret));

            string replaced = "cdnpush_" + Guid.NewGuid().ToString("N");
            store.Write(target, replaced, "robot$pc-b3-ci");
            read = store.Read(target);
            Assert.That(read.Secret, Is.EqualTo(replaced), "a write replaces");
            Assert.That(read.UserName, Is.EqualTo("robot$pc-b3-ci"));

            Assert.That(store.Delete(target), Is.True);
            Assert.That(store.Read(target), Is.Null);
            Assert.That(store.Exists(target), Is.False, "Exists after the delete");
            Assert.That(store.Delete(target), Is.False, "deleting twice reports nothing was stored");
        }

        [Test]
        public void TheWindowsStoreRefusesANameThatIsNotAPingCoreTargetAndAnEmptySecret()
        {
            if (!WindowsCredentialStore.IsSupportedPlatform)
            {
                Assert.Ignore("Windows only.");
            }

            var store = new WindowsCredentialStore();
            Assert.Throws<ArgumentException>(() => store.Write("git:https://github.com", "x"));
            Assert.Throws<ArgumentException>(() => store.Read("Microsoft_OC1"));
            Assert.Throws<ArgumentException>(() => store.Write(target, string.Empty));
            Assert.Throws<CredentialStoreException>(() => store.Write(target, new string('x', 2000)), "over 2560 bytes as UTF-16");
            Assert.That(store.Read(target), Is.Null, "nothing was written");
        }

        [Test]
        public void TheSessionStoreKeepsASecretInMemoryOnly()
        {
            var store = new SessionCredentialStore();
            string secret = "usr_" + Guid.NewGuid().ToString("N");
            try
            {
                Assert.That(store.Exists(target), Is.False);
                store.Write(target, secret, "user");
                Assert.That(store.Exists(target), Is.True);
                Assert.That(store.Read(target).Secret, Is.EqualTo(secret));
                Assert.That(store.Read(target).UserName, Is.EqualTo("user"));
                Assert.That(store.Delete(target), Is.True);
                Assert.That(store.Read(target), Is.Null);
                Assert.That(store.Exists(target), Is.False);
            }
            finally
            {
                store.Delete(target);
            }
        }

        [Test]
        public void TheEditorPrefsFallbackSaysSoInItsDescription()
        {
            var store = new EditorPrefsCredentialStore("the probe failed");
            Assert.That(store.Kind, Is.EqualTo(CredentialStoreKind.EditorPrefs));
            Assert.That(store.Description, Is.EqualTo("Stored in EditorPrefs, not the OS credential store: the probe failed"));
            string secret = "usr_" + Guid.NewGuid().ToString("N");
            try
            {
                store.Write(target, secret);
                Assert.That(store.Exists(target), Is.True);
                Assert.That(store.Read(target).Secret, Is.EqualTo(secret));
            }
            finally
            {
                Assert.That(store.Delete(target), Is.True);
            }

            Assert.That(store.Read(target), Is.Null);
            Assert.That(store.Exists(target), Is.False);
        }
    }
}
