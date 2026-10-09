using NUnit.Framework;
using PingCore.Core;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// The one parse of <c>AGONES_SDK_HTTP_PORT</c> (<see cref="LocalSdkPort.TryParse"/>), which both the local
    /// SDK shim and the heartbeat tier use, so for any value exactly one tier is active. Each tier's own test
    /// only proves it delegates here.
    /// </summary>
    public sealed class LocalSdkPortTests
    {
        [TestCase("1", 1)]
        [TestCase("9358", 9358)]
        [TestCase("65535", 65535)]
        [TestCase("00080", 80)]
        public void APortFromOneTo65535IsAccepted(string raw, int expected)
        {
            Assert.That(LocalSdkPort.TryParse(raw, out int port), Is.True, raw);
            Assert.That(port, Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0")]
        [TestCase("65536")]
        [TestCase("99999")]
        [TestCase("123456")]
        [TestCase("-1")]
        [TestCase("+80")]
        [TestCase(" 9358")]
        [TestCase("9358 ")]
        [TestCase("93a8")]
        [TestCase("0x10")]
        [TestCase("abc")]
        [TestCase("\uFF18\uFF10")]
        public void AnythingElseIsRefusedWithPortZero(string raw)
        {
            // Mutation: parse with NumberStyles.Integer or char.IsDigit and the sign, space or full-width cases pass.
            Assert.That(LocalSdkPort.TryParse(raw, out int port), Is.False, raw ?? "null");
            Assert.That(port, Is.EqualTo(0));
        }

        [Test]
        public void TheVariableIsTheSupervisorsName()
        {
            Assert.That(LocalSdkPort.Variable, Is.EqualTo("AGONES_SDK_HTTP_PORT"));
        }
    }
}
