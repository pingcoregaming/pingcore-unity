using System.Collections.Generic;
using NUnit.Framework;
using PingCore.Discovery.Client;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Workspace.Confirmation;
using PingCore.Editor.Workspace.UI.PlayerHosting;
using PingCore.Editor.Workspace.UI.Common;
using UnityEditor;
using UnityEngine;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// The plugin owns the client settings asset: it uses the project's own wherever it is, picks one
    /// the same way every time when there are several and says so, and creates one under a
    /// <c>Resources/</c> folder only when there is none, which the SDK can then load by name.
    /// </summary>
    public sealed class ClientSettingsAssetTests
    {
        private string scratchFolder;

        [TearDown]
        public void DeleteScratch()
        {
            if (scratchFolder != null && AssetDatabase.IsValidFolder(scratchFolder))
            {
                AssetDatabase.DeleteAsset(scratchFolder);
            }

            scratchFolder = null;
        }

        [Test]
        public void ThePluginsOwnPathWinsElseTheFirstByPath()
        {
            Assert.That(ClientSettingsAsset.DefaultPath, Is.EqualTo("Assets/PingCore/Resources/PingCoreClientSettings.asset"));
            Assert.That(ClientSettingsAsset.Choose(new List<string>()), Is.Null);
            Assert.That(ClientSettingsAsset.Choose(null), Is.Null);
            Assert.That(ClientSettingsAsset.Choose(new[] { "Assets/Z/A.asset", "Assets/B/C.asset" }), Is.EqualTo("Assets/B/C.asset"));
            Assert.That(ClientSettingsAsset.Choose(new[] { "Assets/A/A.asset", ClientSettingsAsset.DefaultPath }), Is.EqualTo(ClientSettingsAsset.DefaultPath));
            Assert.That(ClientSettingsAsset.SeveralNote(new[] { "Assets/A/A.asset" }), Is.Null);
            Assert.That(ClientSettingsAsset.SeveralNote(new[] { "Assets/A/A.asset", "Assets/B/B.asset" }), Does.Contain("uses Assets/A/A.asset").And.Contain("delete the others"));
        }

        [Test]
        public void AnExistingAssetAnywhereIsUsedAndNothingIsCreated()
        {
            IReadOnlyList<string> before = ClientSettingsAsset.FindPaths();
            Assume.That(before, Is.Not.Empty, "the sample project keeps its asset at Assets/Client/Settings/");

            PingCoreClientSettings found = ClientSettingsAsset.FindOrCreate(out string path, out bool created);

            Assert.That(created, Is.False);
            Assert.That(found, Is.Not.Null);
            Assert.That(path, Is.EqualTo(ClientSettingsAsset.Choose(before)));
            Assert.That(ClientSettingsAsset.FindPaths(), Is.EqualTo(before), "no second asset appeared");
        }

        [Test]
        public void ACreatedAssetSitsInAResourcesFolderTheSdkLoadsByName()
        {
            scratchFolder = "Assets/PingCoreSettingsScratch" + System.Guid.NewGuid().ToString("N");
            string path = scratchFolder + "/Resources/" + PingCoreClientSettings.ResourcesName + ".asset";

            PingCoreClientSettings created = ClientSettingsAsset.Create(path);

            Assert.That(created, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(created), Is.EqualTo(path));
            Assert.That(created.FleetAppPublicId, Is.Empty);
            Assert.That(created.OpenRegistrationHeartbeatToken, Is.Empty, "a new asset carries no token");
            Assert.That(ClientSettingsAsset.FindPaths(), Does.Contain(path));
            Assert.That(PingCoreClientSettings.LoadFromResources(), Is.SameAs(created), "Resources.Load finds it by name");
            Assert.Throws<System.ArgumentException>(() => ClientSettingsAsset.Create("Packages/io.pingcore.sdk/x.asset"), "only under Assets/");
        }

        [Test]
        public void ChangingTheCommunityAppClearsATokenThatBelongedToTheOldOne()
        {
            const string OldApp = "dscp_0000000000000000000000000000000a";
            const string NewApp = "dscp_0000000000000000000000000000000b";
            string token = "dsc_" + "testtokenvalue0000000000000000ab";
            var settings = ScriptableObject.CreateInstance<PingCoreClientSettings>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"communityAppPublicId\":\"" + OldApp + "\",\"openRegistrationHeartbeatToken\":\"" + token + "\"}", settings);
                var serialized = new SerializedObject(settings);

                Assert.That(PlayerHostingFold.SetCommunityApp(serialized, OldApp), Is.False, "control: the same app keeps its token");
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(settings.OpenRegistrationHeartbeatToken, Is.EqualTo(token));

                Assert.That(PlayerHostingFold.SetCommunityApp(serialized, NewApp), Is.True);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That((settings.CommunityAppPublicId, settings.OpenRegistrationHeartbeatToken), Is.EqualTo((NewApp, string.Empty)), "the old app's token never ships with the new app");

                JsonUtility.FromJsonOverwrite("{\"communityAppPublicId\":\"\",\"openRegistrationHeartbeatToken\":\"" + token + "\"}", settings);
                serialized = new SerializedObject(settings);
                Assert.That(PlayerHostingFold.SetCommunityApp(serialized, NewApp), Is.False, "a token set while no app was named is kept; Confirm for build checks it against the new app");
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That((settings.CommunityAppPublicId, settings.OpenRegistrationHeartbeatToken), Is.EqualTo((NewApp, token)));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ATokenConfirmedForAnotherAppReadsUnconfirmed()
        {
            const string App = "dscp_0000000000000000000000000000000a";
            string token = "dsc_" + "testtokenvalue0000000000000000ab";
            var confirmations = new List<BuildGuardConfirmation>
            {
                new BuildGuardConfirmation { appPublicId = App, tokenDigest = BuildGuardConfirmations.Digest(token), scope = BuildGuardConfirmations.HeartbeatScope, registrationMode = BuildGuardConfirmations.OpenRegistrationMode, confirmedAt = "2026-10-07T10:00:00Z" },
            };

            Assert.That(PlayerHostingFold.TokenState(token, App, confirmations), Is.EqualTo(ConfirmationState.Confirmed), "control: confirmed for the asset's own app");
            Assert.That(PlayerHostingFold.TokenState(token, "dscp_0000000000000000000000000000000b", confirmations), Is.EqualTo(ConfirmationState.Unconfirmed));
            Assert.That(PlayerHostingFold.TokenState(string.Empty, App, confirmations), Is.EqualTo(ConfirmationState.NoToken));
            Assert.That(PlayerHostingFold.TokenState(token + "x", App, confirmations), Is.EqualTo(ConfirmationState.Unconfirmed));
        }
    }
}
