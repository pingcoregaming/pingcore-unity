using BeaconRush.Hosting;
using NUnit.Framework;
using PingCore.Core.Handshake;

namespace BeaconRush.Tests.Editor
{
    /// <summary>The hosting mode table: what a dedicated game server finds at startup decides its mode, nothing else.</summary>
    public sealed class HostingModeSelectorTests
    {
        private const string Token = "a-heartbeat-token-placeholder";

        [TestCase(true, true, null, "hosted", "local_sdk_endpoint", false, false, Description = "the endpoint answered")]
        [TestCase(true, true, Token, "hosted", "local_sdk_endpoint", false, false, Description = "a token never turns a hosted game server into a self-hosted one")]
        [TestCase(true, false, null, "hosted", "local_sdk_endpoint_unreachable", false, true, Description = "configured but silent fails")]
        [TestCase(true, false, Token, "hosted", "local_sdk_endpoint_unreachable", false, true, Description = "configured but silent never falls back to the heartbeat")]
        [TestCase(false, false, Token, "selfHosted", "discovery_token", false, false, Description = "no endpoint, a token")]
        [TestCase(false, true, Token, "selfHosted", "discovery_token", false, false, Description = "an answer without the endpoint configured cannot happen and does not count")]
        [TestCase(false, false, null, "local", "no_endpoint_no_token", true, false, Description = "neither: a local smoke test")]
        [TestCase(false, false, "", "local", "no_endpoint_no_token", true, false, Description = "an empty token is no token")]
        [TestCase(false, false, "  \t", "local", "no_endpoint_no_token", true, false, Description = "a blank token is no token")]
        public void TheModeFollowsTheTable(bool configured, bool answered, string token, string mode, string reason, bool lanOnly, bool failed)
        {
            HostingSelection selection = HostingModeSelector.Select(configured, answered, token);
            Assert.That(HostingModeSelector.Wire(selection.Mode), Is.EqualTo(mode));
            Assert.That(selection.Reason, Is.EqualTo(reason));
            Assert.That(selection.LanOnly, Is.EqualTo(lanOnly));
            Assert.That(selection.Failed, Is.EqualTo(failed));
        }

        [TestCase(false, "listen_online", HostingMode.Listen)]
        [TestCase(true, "listen_lan", HostingMode.Listen)]
        public void AListenHostIsOnlineOrLanOnly(bool lanOnly, string reason, HostingMode approvalMode)
        {
            HostingSelection selection = HostingModeSelector.ForListen(lanOnly);
            Assert.That(selection.Mode, Is.EqualTo(GameHostingMode.Listen));
            Assert.That(selection.Reason, Is.EqualTo(reason));
            Assert.That(selection.LanOnly, Is.EqualTo(lanOnly));
            Assert.That(selection.ApprovalMode, Is.EqualTo(approvalMode));
            Assert.That(selection.Failed, Is.False);
        }

        [Test]
        public void EachModeAdmitsUnderTheMatchingSdkMode()
        {
            Assert.That(HostingModeSelector.Select(true, true, null).ApprovalMode, Is.EqualTo(HostingMode.Hosted));
            Assert.That(HostingModeSelector.Select(false, false, Token).ApprovalMode, Is.EqualTo(HostingMode.SelfHosted));

            // Unlisted talks to no Discovery, so it admits exactly as a LAN-only listen host: lan tickets only.
            HostingSelection unlisted = HostingModeSelector.Select(false, false, null);
            Assert.That(unlisted.ApprovalMode, Is.EqualTo(HostingMode.Listen));
            Assert.That(unlisted.LanOnly, Is.True);
        }

        [Test]
        public void TheModeLiteralsAreTheEventLiterals()
        {
            Assert.That(HostingModeSelector.Wire(GameHostingMode.Hosted), Is.EqualTo("hosted"));
            Assert.That(HostingModeSelector.Wire(GameHostingMode.SelfHosted), Is.EqualTo("selfHosted"));
            Assert.That(HostingModeSelector.Wire(GameHostingMode.Local), Is.EqualTo("local"));
            Assert.That(HostingModeSelector.Wire(GameHostingMode.Listen), Is.EqualTo("listen"));
            Assert.That(HostingModeSelector.Wire((GameHostingMode)99), Is.EqualTo("unknown"));
        }
    }
}
