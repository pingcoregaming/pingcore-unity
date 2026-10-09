using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Editor.Build;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace BeaconRush.Client.Tests
{
    /// <summary>
    /// The Beacon Rush Server build profile is what the Editor plugin's Ship lists and what the command line for CI and
    /// scripts builds from (<c>-executeMethod PingCore.Editor.Cli.BuildServer.Run -buildProfile "Assets/Settings/Build Profiles/Beacon
    /// Rush Server.asset"</c>): the one Linux Dedicated Server profile in this project, loadable by the command line's own
    /// check, and the one <c>ProjectSettings/PingCoreEditor.json</c> names.
    /// </summary>
    public sealed class ServerBuildProfileTests
    {
        public const string ProfilePath = "Assets/Settings/Build Profiles/Beacon Rush Server.asset";

        [Test]
        public void TheBeaconRushServerProfileIsTheOneLinuxDedicatedServerProfileShipLists()
        {
            Assert.That(BuildProfiles.FindLinuxDedicatedServer().Select(p => p.Path), Is.EqualTo(new[] { ProfilePath }));
        }

        [Test]
        public void TheAgentCommandsProfileCheckAcceptsIt()
        {
            BuildProfile profile = BuildProfiles.LoadServerProfile(ProfilePath, out string problem);
            Assert.That(problem, Is.Null);
            Assert.That(profile, Is.Not.Null);
        }

        [Test]
        public void TheSharedEditorSettingsNameIt()
        {
            string file = Path.Combine(Path.GetDirectoryName(Application.dataPath), "ProjectSettings", "PingCoreEditor.json");
            Assert.That((string)JObject.Parse(File.ReadAllText(file))["buildProfile"], Is.EqualTo(ProfilePath));
        }
    }
}
