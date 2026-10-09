using BeaconRush.Hosting;
using NUnit.Framework;

namespace BeaconRush.Tests.Editor
{
    public sealed class ServerArgsTests
    {
        [Test]
        public void NoPortMeansTheDefault()
        {
            ServerArgs args = ServerArgs.Parse(new[] { "/opt/game/BeaconRushServer.x86_64", "-batchmode", "-nographics" });
            Assert.That(args.IsValid, Is.True);
            Assert.That(args.Port, Is.EqualTo(7777));
            Assert.That(args.PortSource, Is.EqualTo(ServerArgs.PortSourceDefault));
        }

        [Test]
        public void NoArgumentsAtAllMeansTheDefault()
        {
            Assert.That(ServerArgs.Parse(null).Port, Is.EqualTo(ServerArgs.DefaultPort));
        }

        [TestCase("-port", "7001", 7001)]
        [TestCase("-PORT", "1", 1)]
        [TestCase("-Port", "65535", 65535)]
        public void APortArgumentIsRead(string flag, string value, int expected)
        {
            ServerArgs args = ServerArgs.Parse(new[] { "game", flag, value });
            Assert.That(args.IsValid, Is.True, args.Error);
            Assert.That(args.Port, Is.EqualTo(expected));
            Assert.That(args.PortSource, Is.EqualTo(ServerArgs.PortSourceArgument));
        }

        [Test]
        public void TheLastPortWins()
        {
            Assert.That(ServerArgs.Parse(new[] { "-port", "7001", "-port", "7002" }).Port, Is.EqualTo(7002));
        }

        [TestCase("0")]
        [TestCase("65536")]
        [TestCase("-1")]
        [TestCase("+7777")]
        [TestCase("77 77")]
        [TestCase("abc")]
        [TestCase("%GAMEPORT|USERVAL%")]
        [TestCase("")]
        public void AMalformedPortIsAnErrorNotAFallback(string value)
        {
            ServerArgs args = ServerArgs.Parse(new[] { "game", "-port", value });
            Assert.That(args.IsValid, Is.False);
            Assert.That(args.Error, Does.Contain("-port"));
        }

        [Test]
        public void APortFlagWithNoValueIsAnError()
        {
            Assert.That(ServerArgs.Parse(new[] { "game", "-port" }).IsValid, Is.False);
        }

        [Test]
        public void AnUnrelatedFlagEndingInPortIsIgnored()
        {
            ServerArgs args = ServerArgs.Parse(new[] { "game", "-queryport", "9000" });
            Assert.That(args.IsValid, Is.True);
            Assert.That(args.Port, Is.EqualTo(ServerArgs.DefaultPort));
        }

        [TestCase("2026.10.02-abc1234\n", "2026.10.02-abc1234")]
        [TestCase("  v1  \r\nignored", "v1")]
        [TestCase("", null)]
        [TestCase("../x", null)]
        [TestCase("has space", null)]
        [TestCase(null, null)]
        public void TheBuildVersionIsTheFirstLineOfVersionTxtWhenValid(string text, string expected)
        {
            Assert.That(BuildVersionFile.Parse(text), Is.EqualTo(expected));
        }
    }
}
