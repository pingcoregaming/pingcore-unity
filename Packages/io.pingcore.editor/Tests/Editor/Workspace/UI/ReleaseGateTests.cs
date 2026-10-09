using System.Collections.Generic;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.Ship;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// Release is the only Ship step that needs deployments: none disables it with the no-deployment sentence and the deploy
    /// link; deployments on the pushed source release; on another source they warn; a null source with deployments
    /// keeps the old refusal, for Release only.
    /// </summary>
    public sealed class ReleaseGateTests
    {
        private static FleetFacts Facts(int deployments, long fleetSource, string releaseProblem = null)
        {
            var members = new List<FleetDeploymentView>();
            for (int i = 0; i < deployments; i++)
            {
                members.Add(new FleetDeploymentView { BrandDeploymentId = 100 + i });
            }

            return new FleetFacts { FleetId = 1, GameId = 9001, Deployments = members, CdnSourceId = fleetSource, ReleaseProblem = releaseProblem };
        }

        [Test]
        public void NoDeploymentDisablesReleaseWithTheOwnersSentenceAndTheDeployLink()
        {
            ReleaseGateResult gate = ReleaseGate.Evaluate(Facts(0, 0), 31);
            Assert.That(gate.Allowed, Is.False, "[mutation: release onto a fleet with no deployment]");
            Assert.That(gate.Refusal, Is.EqualTo("Pushed builds wait in the CDN source. Add a deployment to the fleet in the panel to run one."));
            Assert.That(gate.OfferDeploy, Is.True);
            Assert.That(gate.Warning, Is.Null);
        }

        [Test]
        public void DeploymentsOnThePushedSourceRelease()
        {
            ReleaseGateResult gate = ReleaseGate.Evaluate(Facts(2, 31), 31);
            Assert.That((gate.Allowed, gate.Refusal, gate.Warning, gate.OfferDeploy), Is.EqualTo((true, (string)null, (string)null, false)));
        }

        [Test]
        public void DeploymentsOnAnotherSourceWarnButDoNotRefuse()
        {
            ReleaseGateResult gate = ReleaseGate.Evaluate(Facts(1, 31), 32);
            Assert.That(gate.Allowed, Is.True);
            Assert.That(gate.Warning, Does.StartWith("This fleet's deployments deliver from CDN source #31, but you pushed to #32."), "[mutation: no warning when the sources differ]");
            Assert.That(ReleaseGate.Evaluate(Facts(1, 31), 0).Warning, Is.Null, "no pushed source known: nothing to compare");
        }

        [Test]
        public void ANullSourceWithDeploymentsKeepsTheRefusalForReleaseOnly()
        {
            ReleaseGateResult gate = ReleaseGate.Evaluate(Facts(2, 0, FleetResolution.MembersDisagreeMessage), 31);
            Assert.That((gate.Allowed, gate.Refusal, gate.OfferDeploy), Is.EqualTo((false, FleetResolution.MembersDisagreeMessage, false)));

            // The same fleet still pushes to its branch: the refusal is Release's alone.
            var facts = Facts(2, 0, FleetResolution.MembersDisagreeMessage);
            facts.Game = new GameBranchesResponse { GameId = 9001, GameBranches = new List<GameBranchView> { new GameBranchView { GameBranchId = 4201, BranchName = "Linux", DataSourceType = "cdn_source", CdnSourceId = 31 } } };
            Assert.That(PushTargetResolution.ForFleet(facts, 0).CanPush, Is.True);
        }

        [Test]
        public void AFleetNotReadYetIsNotReleased()
        {
            Assert.That(ReleaseGate.Evaluate(null, 31).Allowed, Is.False);
        }
    }
}
