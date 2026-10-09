using NUnit.Framework;
using UnityEngine;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// The settings asset's Discovery base URL is a constant: no studio can point a player build at
    /// another host, an older asset's <c>discoveryBaseUrl</c> is ignored, and only the
    /// <c>PINGCORE_DISCOVERY_URL_OVERRIDE</c> define lets an override through.
    /// </summary>
    public sealed class ClientSettingsTests
    {
        [Test]
        public void TheDiscoveryBaseUrlIsTheProductionConstant()
        {
            var settings = ScriptableObject.CreateInstance<PingCoreClientSettings>();
            try
            {
                Assert.That(PingCoreClientSettings.DefaultDiscoveryBaseUrl, Is.EqualTo("https://discovery.pingcore.io"));
                Assert.That(PingCoreClientSettings.DiscoveryUrlOverrideCompiledIn, Is.False, "a normal project compiles without the override define");
                Assert.That(settings.DiscoveryBaseUrl, Is.EqualTo("https://discovery.pingcore.io"));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void AnOlderAssetsDiscoveryBaseUrlIsIgnoredWithoutTheOverrideDefine()
        {
            var settings = ScriptableObject.CreateInstance<PingCoreClientSettings>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"discoveryBaseUrl\":\"https://discovery.evil.test\",\"fleetAppPublicId\":\"dscp_0123456789abcdef0123456789abcdef\"}", settings);
                Assert.That(settings.FleetAppPublicId, Is.EqualTo("dscp_0123456789abcdef0123456789abcdef"), "control: the asset still loads its other fields");
                Assert.That(settings.DiscoveryBaseUrl, Is.EqualTo(PingCoreClientSettings.DefaultDiscoveryBaseUrl), "the old serialized URL never reaches a player build");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [TestCase(false, "https://discovery.example.test", "https://discovery.pingcore.io")]
        [TestCase(false, null, "https://discovery.pingcore.io")]
        [TestCase(true, null, "https://discovery.pingcore.io")]
        [TestCase(true, "   ", "https://discovery.pingcore.io")]
        [TestCase(true, " https://discovery.example.test/ ", "https://discovery.example.test")]
        [TestCase(true, "https://discovery.example.test", "https://discovery.example.test")]
        public void TheOverrideAppliesOnlyWhenCompiledInAndSet(bool compiledIn, string value, string expected)
        {
            Assert.That(PingCoreClientSettings.ResolveDiscoveryBaseUrl(compiledIn, value), Is.EqualTo(expected));
        }

        [Test]
        public void TheAssetHasNoSerializedDiscoveryUrlField()
        {
            var settings = ScriptableObject.CreateInstance<PingCoreClientSettings>();
            try
            {
                string json = JsonUtility.ToJson(settings);
                Assert.That(json, Does.Not.Contain("discoveryBaseUrl"), "the field is gone from the asset");
                Assert.That(json, Does.Contain("fleetAppPublicId").And.Contain("communityAppPublicId").And.Contain("openRegistrationHeartbeatToken"));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}
