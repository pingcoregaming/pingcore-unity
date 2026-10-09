using NUnit.Framework;
using PingCore.Editor.Workspace.UI.Common;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>The panel links: the workspace's own panel from <c>me/capabilities</c> <c>brandUrl</c>, else app.pingcore.io.</summary>
    public sealed class PanelLinksTests
    {
        [TestCase("https://studio.app.pingcore.io", "https://studio.app.pingcore.io", TestName = "a workspace panel")]
        [TestCase("https://Studio.App.PingCore.io/", "https://studio.app.pingcore.io", TestName = "case and a trailing slash")]
        [TestCase("https://games.example.com:8443", "https://games.example.com:8443", TestName = "a custom domain with a port")]
        [TestCase(null, "https://app.pingcore.io", TestName = "no brand URL")]
        [TestCase("", "https://app.pingcore.io", TestName = "an empty brand URL")]
        [TestCase("http://studio.app.pingcore.io", "https://app.pingcore.io", TestName = "plain http")]
        [TestCase("https://studio.app.pingcore.io/fleets", "https://app.pingcore.io", TestName = "a path")]
        [TestCase("https://studio.app.pingcore.io/?next=evil", "https://app.pingcore.io", TestName = "a query")]
        [TestCase("https://user@studio.app.pingcore.io", "https://app.pingcore.io", TestName = "user info")]
        [TestCase("javascript:alert(1)", "https://app.pingcore.io", TestName = "another scheme")]
        [TestCase("studio.app.pingcore.io", "https://app.pingcore.io", TestName = "no scheme")]
        public void ThePanelBaseIsAPlainHttpsHostOrTheDefault(string brandUrl, string expected)
        {
            Assert.That(PanelLinks.BaseFrom(brandUrl), Is.EqualTo(expected));
        }

        [Test]
        public void TheLinksAreThePanelsOwnRoutes()
        {
            const string Base = "https://studio.app.pingcore.io";
            Assert.That(PanelLinks.Fleets(Base), Is.EqualTo("https://studio.app.pingcore.io/fleets"));
            Assert.That(PanelLinks.Fleet(Base, 42), Is.EqualTo("https://studio.app.pingcore.io/fleets/42"));
            Assert.That(PanelLinks.Deploy(Base, 42), Is.EqualTo("https://studio.app.pingcore.io/mygames/deployments/deploy?fleetId=42"), "the existing deploy page, preset to the fleet");
            Assert.That(PanelLinks.Deployment(Base, 21), Is.EqualTo("https://studio.app.pingcore.io/mygames/deployments/21"));
            Assert.That(PanelLinks.Branch(Base, 9001, 4201), Is.EqualTo("https://studio.app.pingcore.io/mygames/9001/branches/4201/edit"), "the panel's BranchEdit route, :gameId/branches/:branchId/edit");
            Assert.That(PanelLinks.GameBranches(Base, 9001), Is.EqualTo("https://studio.app.pingcore.io/mygames/9001/branches"));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => PanelLinks.Branch(Base, 9001, 0));
            Assert.That(PanelLinks.Fleets(null), Is.EqualTo("https://app.pingcore.io/fleets"));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => PanelLinks.Fleet(Base, 0));
        }
    }
}
