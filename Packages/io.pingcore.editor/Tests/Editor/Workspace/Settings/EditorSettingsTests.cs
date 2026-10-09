using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PingCore.Editor.Workspace.Settings;

namespace PingCore.Editor.Workspace.Tests.Settings
{
    /// <summary>The two settings files: their paths, round trip, format marker and the refusal of anything credential-shaped.</summary>
    public sealed class EditorSettingsTests
    {
        private string root;

        [SetUp]
        public void SetUp() => root = Path.Combine(Path.GetTempPath(), "pingcore-settings-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ProjectSettingsRoundTripInProjectSettingsWithIdsOnly()
        {
            var settings = new EditorProjectSettings
            {
                GameId = 9001,
                FleetId = 1,
                GameBranchId = 4201,
                BuildProfile = "Assets/Settings/Build Profiles/Linux Server.asset",
                ProcessName = "./BeaconRushServer.x86_64",
            };
            settings.Save(root);

            string path = Path.Combine(root, "ProjectSettings", "PingCoreEditor.json");
            Assert.That(File.Exists(path), Is.True);
            string text = File.ReadAllText(path);
            Assert.That(text, Does.StartWith("{\n  \"format\": \"pingcore-editor-settings/1\""));
            Assert.That(text, Does.Not.Contain("\r"));

            EditorProjectSettings back = EditorProjectSettings.Load(root);
            Assert.That(text, Does.Not.Contain("workspaceHost"), "there is no workspace URL any more");
            Assert.That(back.GameId, Is.EqualTo(9001));
            Assert.That(back.FleetId, Is.EqualTo(1));
            Assert.That(back.GameBranchId, Is.EqualTo(4201), "Push to branch is remembered as an id");
            Assert.That(text, Does.Contain("\"gameBranchId\": 4201"));
            Assert.That(back.BuildProfile, Is.EqualTo("Assets/Settings/Build Profiles/Linux Server.asset"));
            Assert.That(back.ProcessName, Is.EqualTo("./BeaconRushServer.x86_64"), "the picked server executable is remembered as the process name");
            Assert.That(text, Does.Contain("\"processName\": \"./BeaconRushServer.x86_64\""));
            new EditorProjectSettings { GameId = 9001 }.Save(root);
            Assert.That(File.ReadAllText(path), Does.Not.Contain("processName"), "no pick, no key");
            Assert.That(text, Does.Not.Contain("cdnSourceId").And.Not.Contain("pushPath"), "the CDN source comes from the fleet's build targets, and there is one push path");
        }

        [Test]
        public void AnEarlierFilesWorkspaceHostIsIgnoredOnLoadAndDroppedOnSave()
        {
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            string path = Path.Combine(root, "ProjectSettings", "PingCoreEditor.json");
            File.WriteAllText(path, "{\"format\":\"pingcore-editor-settings/1\",\"workspaceHost\":\"studio.app.pingcore.io\",\"fleetId\":42}");

            EditorProjectSettings loaded = EditorProjectSettings.Load(root);
            Assert.That(loaded.FleetId, Is.EqualTo(42));
            Assert.That(loaded.GameId, Is.EqualTo(0), "an earlier file names no game; the fleet does");
            loaded.Save(root);
            Assert.That(File.ReadAllText(path), Does.Not.Contain("workspaceHost"));
        }

        [Test]
        public void TheApiBaseOverrideIsHttpsOnlyAndAbsentByDefault()
        {
            Assert.That(new EditorUserSettings().ApiBaseOverride, Is.Null);
            new EditorUserSettings().Save(root);
            Assert.That(File.ReadAllText(EditorUserSettings.PathIn(root)), Does.Not.Contain("apiBaseOverride"), "no override is written unless one is set");

            new EditorUserSettings { ApiBaseOverride = "https://api.example.test/api" }.Save(root);
            Assert.That(EditorUserSettings.Load(root).ApiBaseOverride, Is.EqualTo("https://api.example.test/api"));

            var refused = Assert.Throws<InvalidOperationException>(() => new EditorUserSettings { ApiBaseOverride = "http://api.example.test" }.Save(root));
            Assert.That(refused.Message, Does.Contain("https"));
            Assert.That(EditorUserSettings.Load(root).ApiBaseOverride, Is.EqualTo("https://api.example.test/api"), "[mutation: save the http override] the refused value never reached the file");
        }

        [Test]
        public void UserSettingsLiveInUserSettings()
        {
            new EditorUserSettings { PingctlPath = "C:/tools/pingctl.exe", SessionOnlyKey = true }.Save(root);
            Assert.That(File.Exists(Path.Combine(root, "UserSettings", "PingCoreEditorUser.json")), Is.True);
            EditorUserSettings back = EditorUserSettings.Load(root);
            Assert.That(back.PingctlPath, Is.EqualTo("C:/tools/pingctl.exe"));
            Assert.That(back.SessionOnlyKey, Is.True);
            Assert.That(EditorUserSettings.Load(Path.Combine(root, "missing")).PingctlPath, Is.Null, "absent file gives defaults");
        }

        [Test]
        public void ACredentialShapedValueIsRefusedAndNothingIsWritten()
        {
            string key = "usr_" + new string('m', 20);
            var settings = new EditorProjectSettings { BuildProfile = "Assets/" + key + ".asset" };
            var e = Assert.Throws<InvalidOperationException>(() => settings.Save(root));
            Assert.That(e.Message, Does.Contain("buildProfile").And.Not.Contain(key));
            Assert.That(Directory.Exists(root), Is.False);

            var user = new EditorUserSettings { PushFolder = "C:/x/" + new string('a', 64) };
            Assert.Throws<InvalidOperationException>(() => user.Save(root), "a 64-hex run is a credential shape too");
            Assert.That(Directory.Exists(root), Is.False);
        }

        [TestCase("usr", 8, true)]
        [TestCase("cdnpush", 8, true)]
        [TestCase("dsc", 12, true)]
        [TestCase("usr", 7, false)]
        public void SettingsRefuseTheSameShortTokenShapeTheCommandLineDoes(string prefix, int tail, bool refused)
        {
            string value = "C:/tools/" + prefix + "_" + new string('k', tail);
            var user = new EditorUserSettings { PingctlPath = value };
            if (refused)
            {
                Assert.Throws<InvalidOperationException>(() => user.Save(root), "[mutation: refuse only the 16-character mask shape]");
                Assert.That(Directory.Exists(root), Is.False);
            }
            else
            {
                Assert.DoesNotThrow(() => user.Save(root));
            }

            Assert.That(global::PingCore.Editor.Workspace.Redaction.Redactor.LooksLikeCredentialArgument(value), Is.EqualTo(refused), "one rule for arguments and settings");
        }

        [Test]
        public void AWrongFormatOrBadJsonIsInvalidData()
        {
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            File.WriteAllText(Path.Combine(root, "ProjectSettings", "PingCoreEditor.json"), "{\"format\":\"something-else/1\"}");
            Assert.Throws<InvalidDataException>(() => EditorProjectSettings.Load(root));
            File.WriteAllText(Path.Combine(root, "ProjectSettings", "PingCoreEditor.json"), "{ not json");
            Assert.Throws<InvalidDataException>(() => EditorProjectSettings.Load(root));
        }

        [Test]
        public void ABuildProfileOutsideAssetsOrANegativeIdIsRefused()
        {
            Assert.Throws<InvalidOperationException>(() => new EditorProjectSettings { BuildProfile = "Packages/x/Server.asset" }.Validate());
            Assert.Throws<InvalidOperationException>(() => new EditorProjectSettings { BuildProfile = "Assets/Server.unity" }.Validate());
            Assert.Throws<InvalidOperationException>(() => new EditorProjectSettings { FleetId = -1 }.Validate());
            Assert.Throws<InvalidOperationException>(() => new EditorProjectSettings { GameId = -1 }.Validate());
            Assert.Throws<InvalidOperationException>(() => new EditorProjectSettings { GameBranchId = -1 }.Validate());
            Assert.Throws<InvalidOperationException>(() => new EditorProjectSettings { ProcessName = "./Game\n-x" }.Validate(), "one line");
            Assert.Throws<InvalidOperationException>(() => new EditorProjectSettings { ProcessName = new string('a', 257) }.Validate());
            Assert.DoesNotThrow(() => new EditorProjectSettings { BuildProfile = "Assets/Server.asset" }.Validate());
            Assert.DoesNotThrow(() => new EditorProjectSettings().Validate(), "no profile picked yet");
        }

        [Test]
        public void AnEarlierVersionsFieldsAreIgnoredOnLoadAndDroppedOnSave()
        {
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "UserSettings"));
            File.WriteAllText(EditorProjectSettings.PathIn(root), "{\"format\":\"pingcore-editor-settings/1\",\"gameId\":9001,\"fleetId\":42,\"cdnSourceId\":3001,\"productName\":\"BeaconRushServer\",\"scenes\":[],\"supervisorBaseTag\":null,\"pushPath\":\"image\"}");
            File.WriteAllText(EditorUserSettings.PathIn(root), "{\"format\":\"pingcore-editor-user/1\",\"dockerPath\":\"C:/docker.exe\",\"registryCredentialIds\":{\"studio.app.pingcore.io\":5}}");

            EditorProjectSettings project = EditorProjectSettings.Load(root);
            Assert.That(project.FleetId, Is.EqualTo(42));
            Assert.That(project.GameId, Is.EqualTo(9001));
            project.Save(root);
            EditorUserSettings.Load(root).Save(root);

            Assert.That(File.ReadAllText(EditorProjectSettings.PathIn(root)), Does.Not.Contain("cdnSourceId").And.Not.Contain("productName").And.Not.Contain("pushPath").And.Not.Contain("supervisorBaseTag"));
            Assert.That(File.ReadAllText(EditorUserSettings.PathIn(root)), Does.Not.Contain("dockerPath").And.Not.Contain("registryCredentialIds"));
        }
    }
}
