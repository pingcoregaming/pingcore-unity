using BeaconRush.Client.Models;
using NUnit.Framework;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client;

namespace BeaconRush.Client.Tests
{
    /// <summary>
    /// Beacon Rush's missing-infrastructure banner: an empty or placeholder fleet app id is "not connected", the menu shows
    /// the SDK check's message with the Editor's detail under it, and a failed join shows the same message when the check
    /// explains it, else the join's own words.
    /// </summary>
    public sealed class InfrastructureBannerTests
    {
        private static InfrastructureReport Report(InfrastructureState state, string detail = null)
        {
            DiscoveryCallResult answer = state == InfrastructureState.DiscoveryError ? new DiscoveryCallResult(DiscoveryOutcome.Degraded, 503, null, "x", null, null, null) : null;
            return new InfrastructureReport(state, answer, state == InfrastructureState.Ok ? 2 : 0, detail);
        }

        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase(ClientApps.PlaceholderPublicId, "")]
        [TestCase("dscp_0123456789abcdef0123456789abcdef", "dscp_0123456789abcdef0123456789abcdef")]
        [TestCase(" dscp_0123456789abcdef0123456789abcdef ", " dscp_0123456789abcdef0123456789abcdef ", Description = "kept as it is: the check reads it as not a public id")]
        public void TheShippedEmptyOrPlaceholderIdIsNotConnected(string configured, string expected)
        {
            Assert.That(InfrastructureBanner.AppIdToCheck(configured), Is.EqualTo(expected));
        }

        [Test]
        public void TheBannerShowsTheMessageAndTheEditorDetailUntilTheCheckIsOk()
        {
            Assert.That(InfrastructureBanner.Headline(null), Is.Null, "nothing before the first check");
            Assert.That(InfrastructureBanner.Headline(Report(InfrastructureState.Ok)), Is.Null);
            InfrastructureReport noServers = Report(InfrastructureState.NoGameServers, "Fleet Beacon Rush has no deployment.");
            Assert.That(InfrastructureBanner.Headline(noServers), Is.EqualTo(InfrastructureCheck.NoGameServersMessage));
            Assert.That(InfrastructureBanner.Detail(noServers), Is.EqualTo("Fleet Beacon Rush has no deployment."));
            Assert.That(InfrastructureBanner.Detail(Report(InfrastructureState.NoAppId)), Is.Null, "a player build has no detail");
            Assert.That(InfrastructureBanner.Headline(Report(InfrastructureState.NoAppId)), Is.EqualTo(InfrastructureCheck.NoAppIdMessage));
        }

        [TestCase(InfrastructureState.NoAppId, true, true)]
        [TestCase(InfrastructureState.AppUnknown, true, true)]
        [TestCase(InfrastructureState.NoGameServers, true, false)]
        [TestCase(InfrastructureState.Unreachable, true, true)]
        [TestCase(InfrastructureState.DiscoveryError, false, false)]
        [TestCase(InfrastructureState.Ok, false, false)]
        public void AFailedJoinShowsTheCheckWhenItExplainsTheFailure(InfrastructureState state, bool explains, bool blocksMatchmaking)
        {
            Assert.That(InfrastructureBanner.BlocksMatchmaking(Report(state)), Is.EqualTo(blocksMatchmaking), "no game server listed still queues: draining game servers are hidden");
            InfrastructureReport report = Report(state, state == InfrastructureState.NoGameServers ? "Its deployment is starting (0 of 1 ready)." : null);
            string shown = InfrastructureBanner.JoinFailure(report, "Quick play could not find a game server: Conflict (409): no_seats");
            Assert.That(InfrastructureBanner.Explains(report), Is.EqualTo(explains));
            Assert.That(shown, Is.EqualTo(explains ? report.ToString() : "Quick play could not find a game server: Conflict (409): no_seats"));
            Assert.That(InfrastructureBanner.JoinFailure(null, "own words"), Is.EqualTo("own words"), "no report, the join's own words");
        }
    }
}
