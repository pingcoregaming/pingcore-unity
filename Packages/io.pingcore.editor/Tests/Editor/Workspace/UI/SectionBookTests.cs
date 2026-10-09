using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Confirmation;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.Ship;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>The section model of Window > PingCore as a table: each section's status, line and one next action.</summary>
    public sealed class SectionBookTests
    {
        // A project signed in, connected to a CDN fleet with a deployment, a profile picked and pingctl found.
        private static SectionFacts Ready() => new SectionFacts
        {
            SignedIn = true,
            SignedInLine = "Signed in to Studio as dev@studio.test.",
            FleetCount = 2,
            FleetId = 42,
            FleetName = "Beacon Rush EU",
            FleetResolved = true,
            DeploymentCount = 1,
            BuildProfileChosen = true,
            PingctlFound = true,
        };

        private static SectionState Of(SectionFacts f, Section section) => SectionBook.Evaluate(f).Single(s => s.Section == section);

        [Test]
        public void TheFourSectionsComeInTheSpecsOrder()
        {
            Assert.That(SectionBook.Evaluate(new SectionFacts()).Select(s => s.Section), Is.EqualTo(new[] { Section.Connect, Section.Ship, Section.Status, Section.PlayerHosting }));
            Assert.That(SectionBook.Evaluate(null).Count, Is.EqualTo(4));
            Assert.That(SectionBook.All.Select(SectionBook.What), Has.All.Not.Empty);
        }

        [Test]
        public void SignedOutOnlyConnectAsksForAnythingAndShipAndStatusWaitForIt()
        {
            var f = new SectionFacts();
            Assert.That((Of(f, Section.Connect).Status, Of(f, Section.Connect).NextAction), Is.EqualTo((SectionStatus.ToDo, "Paste a brand member's usr_ key and press Sign in.")));
            Assert.That(Of(f, Section.Ship).Status, Is.EqualTo(SectionStatus.Blocked));
            Assert.That(Of(f, Section.Ship).Enabled, Is.False, "a blocked section is folded and disabled");
            Assert.That(Of(f, Section.Ship).Chip, Is.EqualTo("connect first"));
            Assert.That(Of(f, Section.Status).Status, Is.EqualTo(SectionStatus.Blocked));
            Assert.That(Of(f, Section.PlayerHosting).Status, Is.EqualTo(SectionStatus.Off), "the fold is off by default");
        }

        [Test]
        public void AWorkspaceWithNoFleetIsSentToThePanelsFleetsPage()
        {
            SectionFacts f = Ready();
            f.FleetId = 0;
            f.FleetCount = 0;
            SectionState connect = Of(f, Section.Connect);
            Assert.That(connect.Line, Is.EqualTo("Set up your game and a fleet in the panel first."));
            Assert.That(connect.NextAction, Does.Contain("Open Fleets in the panel"));
            Assert.That(Of(f, Section.Ship).Status, Is.EqualTo(SectionStatus.Blocked));

            f.FleetCount = 3;
            Assert.That(Of(f, Section.Connect).NextAction, Is.EqualTo("Pick the fleet this project ships to."));
        }

        [Test]
        public void AFleetWithNoDeploymentLeavesConnectDoneAndStatusSaysNoGameServersAreRunning()
        {
            SectionFacts f = Ready();
            f.DeploymentCount = 0;
            f.ReleaseProblem = ReleaseGate.NoDeploymentMessage;
            SectionState connect = Of(f, Section.Connect);
            Assert.That((connect.Status, connect.Line, connect.NextAction), Is.EqualTo((SectionStatus.Done, "This project ships to Beacon Rush EU.", (string)null)),
                "[mutation: no deployment makes Connect to do] signed in with a fleet picked is all Connect asks");
            SectionState status = Of(f, Section.Status);
            Assert.That((status.Status, status.Chip), Is.EqualTo((SectionStatus.ToDo, "to do")));
            Assert.That(status.Line, Is.EqualTo("Your fleet has no deployment, so no game servers are running. Add one in the panel."));
            Assert.That(status.NextAction, Is.EqualTo("Add a deployment in the panel."));
            Assert.That(status.Enabled, Is.True, "Status unfolds with its deploy link");
            SectionState ship = Of(f, Section.Ship);
            Assert.That(ship.Status, Is.EqualTo(SectionStatus.Ready), "[mutation: no deployment blocks Push] Build and Push never depend on deployments");
            Assert.That(ship.Line, Does.Not.Contain("deployment"));
            Assert.That(ship.NextAction, Is.EqualTo("Build, then Push. Release waits for a deployment; see Release below."));

            f.FleetResolved = false;
            Assert.That(Of(f, Section.Status).Status, Is.EqualTo(SectionStatus.Done), "before the fleet is read nothing is claimed about it");
            Assert.That(Of(f, Section.Connect).Status, Is.EqualTo(SectionStatus.Done));
        }

        [TestCase(true, 0, SectionStatus.Done, TestName = "Connect: signed in, a fleet picked, no deployment: done")]
        [TestCase(true, 2, SectionStatus.Done, TestName = "Connect: signed in, a fleet picked, deployments: done")]
        [TestCase(false, 1, SectionStatus.Done, TestName = "Connect: a fleet picked and not read yet: done")]
        public void ConnectIsDoneOnceSignedInWithAFleetPickedWhateverItsDeployments(bool resolved, int deployments, SectionStatus expected)
        {
            SectionFacts f = Ready();
            f.FleetResolved = resolved;
            f.DeploymentCount = deployments;
            Assert.That(Of(f, Section.Connect).Status, Is.EqualTo(expected));
        }

        [Test]
        public void ShipIsNeverReadyBeforeTheFleetIsRead()
        {
            SectionFacts f = Ready();
            f.FleetResolved = false;
            SectionState ship = Of(f, Section.Ship);
            Assert.That(ship.Status, Is.EqualTo(SectionStatus.ToDo), "[mutation: Ready before the CDN source is known]");
            Assert.That(ship.Line, Is.EqualTo("The fleet's game is not read yet, so the branch and CDN source to push to are not known."));
        }

        [Test]
        public void AnImageBranchPutsShipOutsideThePluginWithTheSpecsSentence()
        {
            SectionFacts f = Ready();
            f.OutsideThePlugin = true;
            f.PushProblem = DataSourceRule.ImageMessage;
            SectionState ship = Of(f, Section.Ship);
            Assert.That((ship.Status, ship.Chip, ship.NextAction), Is.EqualTo((SectionStatus.Unavailable, "outside the plugin", (string)null)));
            Assert.That(ship.Line, Is.EqualTo("This fleet runs a container image. The plugin pushes to CDN sources only; push your image and release it outside the plugin."));
            Assert.That(ship.Enabled, Is.True, "Build still works; only Push and Release refuse");
            f.BranchCount = 2;
            Assert.That(Of(f, Section.Ship).NextAction, Is.EqualTo("Pick another branch under Ship, Push to branch."), "another branch may be on a CDN source");
            Assert.That(Of(f, Section.Connect).Status, Is.EqualTo(SectionStatus.Done));
        }

        [TestCase(false, true, null, SectionStatus.ToDo, "No Linux Dedicated Server build profile is picked.", TestName = "no build profile")]
        [TestCase(true, false, null, SectionStatus.ToDo, "The bundled pingctl does not match", TestName = "a pingctl that may not run")]
        [TestCase(true, true, "Branch Main has no data source, so there is nowhere to push.", SectionStatus.ToDo, "Branch Main has no data source", TestName = "a branch with no data source")]
        [TestCase(true, true, null, SectionStatus.Ready, "Ready to ship to Beacon Rush EU.", TestName = "ready")]
        public void ShipIsReadyOnlyWithAProfileARunnablePingctlAndABranchOnACdnSource(bool profile, bool pingctl, string problem, SectionStatus status, string line)
        {
            SectionFacts f = Ready();
            f.BuildProfileChosen = profile;
            f.PingctlFound = pingctl;
            f.PingctlProblem = pingctl ? null : "The bundled pingctl does not match its pinned SHA-256.";
            f.PushProblem = problem;
            SectionState ship = Of(f, Section.Ship);
            Assert.That(ship.Status, Is.EqualTo(status));
            Assert.That(ship.Line, Does.StartWith(line));
            Assert.That(ship.NextAction, Is.Not.Null, "every ship state asks for one thing");
        }

        [Test]
        public void SeveralBranchesAskForThePickAndAReleaseProblemNeverHoldsShipBack()
        {
            SectionFacts f = Ready();
            f.PushProblem = "The game has several branches: pick the branch to push to.";
            f.BranchToPick = true;
            Assert.That((Of(f, Section.Ship).Status, Of(f, Section.Ship).NextAction), Is.EqualTo((SectionStatus.ToDo, "Pick the branch under Ship, Push to branch.")));

            SectionFacts mixed = Ready();
            mixed.ReleaseProblem = FleetResolution.MembersDisagreeMessage;
            Assert.That(Of(mixed, Section.Ship).Status, Is.EqualTo(SectionStatus.Ready), "members on different CDN sources hold back Release only");
            Assert.That(Of(mixed, Section.Ship).NextAction, Is.EqualTo("Build, then Push. Release cannot run yet; see Release below."),
                "[mutation: say the fleet waits for a deployment] this fleet has one");
        }

        [Test]
        public void StatusClaimsNothingAboutDeploymentsBeforeConnectIsDone()
        {
            SectionFacts signedOut = new SectionFacts { FleetId = 42, FleetResolved = true, DeploymentCount = 0 };
            Assert.That(Of(signedOut, Section.Status).Status, Is.EqualTo(SectionStatus.Blocked), "no deployment is not claimed before Connect is done");
        }

        [Test]
        public void StatusIsAReadOnlyViewOnceConnected()
        {
            SectionState status = Of(Ready(), Section.Status);
            Assert.That((status.Status, status.NextAction), Is.EqualTo((SectionStatus.Done, (string)null)));
            Assert.That(status.Line, Does.Contain("Beacon Rush EU").And.Contain("read only"));
        }

        [TestCase(false, ConfirmationState.NoToken, SectionStatus.Off, TestName = "nothing set: off")]
        [TestCase(true, ConfirmationState.NoToken, SectionStatus.Done, TestName = "a community app with the token from the environment")]
        [TestCase(true, ConfirmationState.Confirmed, SectionStatus.Done, TestName = "a confirmed token")]
        [TestCase(true, ConfirmationState.Unconfirmed, SectionStatus.ToDo, TestName = "an unconfirmed token must be confirmed or cleared")]
        [TestCase(false, ConfirmationState.Unconfirmed, SectionStatus.ToDo, TestName = "an unconfirmed token even with no app")]
        public void PlayerHostingIsOffUntilSomethingIsSetAndAnUnconfirmedTokenIsToDo(bool communityApp, ConfirmationState token, SectionStatus status)
        {
            SectionFacts f = Ready();
            f.CommunityAppSet = communityApp;
            f.HeartbeatToken = token;
            SectionState hosting = Of(f, Section.PlayerHosting);
            Assert.That(hosting.Status, Is.EqualTo(status));
            Assert.That(hosting.NextAction == null, Is.EqualTo(status != SectionStatus.ToDo), "only a to-do asks for something");
        }
    }
}
