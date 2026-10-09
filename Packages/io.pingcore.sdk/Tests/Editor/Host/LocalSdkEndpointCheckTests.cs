using System.Collections.Generic;
using NUnit.Framework;

namespace PingCore.Discovery.Host.Tests.Editor
{
    public sealed class LocalSdkEndpointCheckTests
    {
        [Test]
        public void TheCheckUsesTheSharedPortParse()
        {
            // The table lives once, in LocalSdkPortTests; this proves the heartbeat tier delegates to it.
            foreach (string raw in new[] { "9358", "0", "+80", "８０", null })
            {
                Assert.That(LocalSdkEndpointCheck.NamesPort(raw), Is.EqualTo(PingCore.Core.LocalSdkPort.TryParse(raw, out _)), raw ?? "null");
            }

            Assert.That(LocalSdkEndpointCheck.PortVariable, Is.EqualTo(PingCore.Core.LocalSdkPort.Variable));
        }

        [Test]
        public void TheCheckReadsOnlyAgonesSdkHttpPort()
        {
            var reads = new List<string>();
            bool present = LocalSdkEndpointCheck.IsPresent(name =>
            {
                reads.Add(name);
                return "9358";
            });

            Assert.That(present, Is.True);
            Assert.That(reads, Is.EqualTo(new[] { "AGONES_SDK_HTTP_PORT" }));
            Assert.That(LocalSdkEndpointCheck.IsPresent(null), Is.False);
        }
    }
}
