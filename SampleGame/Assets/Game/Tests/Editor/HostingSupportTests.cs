using System;
using BeaconRush.Hosting;
using BeaconRush.Match;
using NUnit.Framework;
using PingCore.Discovery.Host;

namespace BeaconRush.Tests.Editor
{
    /// <summary>The heartbeat start retries, the display name cleaner and the event literals.</summary>
    public sealed class HostingSupportTests
    {
        [TestCase(1, 5)]
        [TestCase(2, 10)]
        [TestCase(3, 20)]
        [TestCase(4, 30)]
        [TestCase(40, 30)]
        [TestCase(0, 0)]
        public void AFailedHeartbeatStartIsRetriedOnTheSchedule(int failures, int seconds)
        {
            Assert.That(HeartbeatStartRetry.DelayAfter(failures), Is.EqualTo(TimeSpan.FromSeconds(seconds)));
        }

        [TestCase(HeartbeatStartOutcome.Failed, true)]
        [TestCase(HeartbeatStartOutcome.Started, false)]
        [TestCase(HeartbeatStartOutcome.Refused, false)]
        [TestCase(HeartbeatStartOutcome.LocalSdkEndpointPresent, false)]
        public void OnlyAFailedStartIsRetried(HeartbeatStartOutcome outcome, bool retried)
        {
            Assert.That(HeartbeatStartRetry.Retries(outcome), Is.EqualTo(retried));
        }

        [TestCase("Ada", "Ada")]
        [TestCase("  Ada   Lovelace ", "Ada Lovelace")]
        [TestCase("Ada\u0000\u0007​", "Ada")]
        [TestCase("\t\n", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void ADisplayNameIsCleaned(string raw, string clean)
        {
            Assert.That(DisplayNames.Clean(raw), Is.EqualTo(clean));
        }

        [Test]
        public void ALongNameIsCutAtACharacterBoundaryToFitTheFixedString()
        {
            string name = DisplayNames.Clean(new string('é', 40));
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(name), Is.LessThanOrEqualTo(DisplayNames.MaxBytes));
            Assert.That(name, Is.EqualTo(new string('é', 14)), "two bytes each, so 14 fit in 29");
            Assert.That(DisplayNames.ToFixed(new string('x', 64), 3).ToString(), Has.Length.EqualTo(DisplayNames.MaxBytes));
        }

        [Test]
        public void AnEmptyNameShowsTheClientId()
        {
            Assert.That(DisplayNames.ForClient(null, 7), Is.EqualTo("Player 7"));
            Assert.That(DisplayNames.ForClient("\u0001", ulong.MaxValue), Is.EqualTo("Player 18446744073709551615"));
            Assert.That(DisplayNames.ToFixed(null, ulong.MaxValue).ToString(), Is.EqualTo("Player 18446744073709551615"));
        }

        [Test]
        public void EnumLiteralsStartLowercase()
        {
            Assert.That(EventWire.Camel(HeartbeatStartOutcome.LocalSdkEndpointPresent), Is.EqualTo("localSdkEndpointPresent"));
            Assert.That(EventWire.Camel(null), Is.Null);
        }
    }
}
