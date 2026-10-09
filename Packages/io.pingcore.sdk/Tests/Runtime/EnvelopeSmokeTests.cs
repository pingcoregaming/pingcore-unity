using Newtonsoft.Json;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Sdk.Tests.Runtime
{
    /// <summary>
    /// PlayMode test: the Discovery error envelope parses in a player runtime with the runtime
    /// settings (which ignore additive fields).
    /// </summary>
    public sealed class EnvelopeSmokeTests
    {
        [Test]
        public void AnErrorEnvelopeParsesWithItsReasonInThePlayerRuntime()
        {
            const string body = "{\"error\":true,\"message\":\"No seats left.\",\"reason\":\"no_seats\",\"available\":0,\"addedLater\":1}";
            var envelope = JsonConvert.DeserializeObject<ErrorEnvelope>(body, PingCoreJson.Settings);

            Assert.That(envelope.Error, Is.True);
            Assert.That(envelope.Message, Is.EqualTo("No seats left."));
            Assert.That(envelope.ReasonCode, Is.EqualTo(DiscoveryReason.NoSeats));
            Assert.That(envelope.Available, Is.EqualTo(0));
            Assert.That(envelope.Limit, Is.Null);
        }
    }
}
