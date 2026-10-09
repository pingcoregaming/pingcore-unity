using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.Tests.Fakes;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.Ship;
using PingCore.Editor.Workspace.UI.Status;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// A picked fleet answers everything else: the game, the app id and the deployments from <c>GET fleets/{id}</c>, the
    /// game's branches from <c>GET my-games/{id}</c>, and, for Release only, the data source and CDN source its
    /// deployments deliver from (their build targets), never guessed. Built on the pinned fixtures.
    /// </summary>
    public sealed class FleetResolutionTests
    {
        private static FleetDetailResponse Detail() => WorkspaceFixtures.Payload<FleetDetailResponse>("fleets.get.json");

        private static BuildTargetsResponse Cdn() => WorkspaceFixtures.Payload<BuildTargetsResponse>("build-targets.cdn.json");

        private static GameBranchesResponse Game() => WorkspaceFixtures.Payload<GameBranchesResponse>("game.get.json");

        [Test]
        public void ACdnFleetGivesItsGameItsAppItsDeploymentsAndItsOneCdnSource()
        {
            FleetFacts facts = FleetResolution.Resolve(1, Detail(), Cdn(), null);
            Assert.That((facts.FleetId, facts.FleetName, facts.GameId, facts.GameName), Is.EqualTo((1L, "EU Matchmaking", 1L, "Minecraft")));
            Assert.That(facts.AppPublicId, Is.EqualTo("dscp_0123456789abcdef0123456789abcdef"));
            Assert.That(facts.Deployments.Select(d => d.BrandDeploymentId), Is.EqualTo(new[] { 100L }));
            Assert.That((facts.DataSource, facts.CdnSourceId), Is.EqualTo(("cdn_source", 31L)));
            Assert.That((facts.ReleaseProblem, facts.Note, facts.GameProblem), Is.EqualTo(((string)null, (string)null, (string)null)));
        }

        [TestCase("dscp_0123456789abcdef0123456789abcdef", "dscp_0123456789abcdef0123456789abcdef", TestName = "a public id")]
        [TestCase(" dscp_abc ", "dscp_abc", TestName = "a public id with spaces")]
        [TestCase("dsc" + "_0123456789abcdef0123456789abcdef", null, TestName = "an app token is never a public id")]
        [TestCase("dscp_../x", null, TestName = "not letters and digits")]
        [TestCase("", null, TestName = "empty")]
        public void OnlyADiscoveryPublicIdReachesTheClientSettingsAsset(string fromTheAnswer, string expected)
        {
            FleetDetailResponse detail = Detail();
            detail.Fleet.DiscoveryPublicId = fromTheAnswer;
            Assert.That(FleetResolution.Resolve(1, detail, Cdn(), null).AppPublicId, Is.EqualTo(expected));
        }

        [Test]
        public void AFleetWhoseDeploymentsRunAnImageRefusesReleaseInTheSpecsWords()
        {
            FleetFacts facts = FleetResolution.Resolve(1, Detail(), WorkspaceFixtures.Payload<BuildTargetsResponse>("build-targets.image.json"), null);
            Assert.That(facts.CdnSourceId, Is.Zero);
            Assert.That(facts.ReleaseProblem, Is.EqualTo("This fleet runs a container image. The plugin pushes to CDN sources only; push your image and release it outside the plugin."));
            Assert.That(facts.Note, Is.Null, "Push is the branch's business, not the deployments'");
        }

        [Test]
        public void ACdnFleetWhoseMembersDisagreeIsRefusedNeverGuessed()
        {
            BuildTargetsResponse mixed = Cdn();
            mixed.CdnSourceId = null;
            FleetFacts facts = FleetResolution.Resolve(1, Detail(), mixed, null);
            Assert.That(facts.CdnSourceId, Is.Zero, "[mutation: fall back to the first member's source]");
            Assert.That(facts.ReleaseProblem, Is.EqualTo(FleetResolution.MembersDisagreeMessage));
            Assert.That(facts.ReleaseProblem, Does.Contain("Give them the same CDN source in the panel"));
        }

        [Test]
        public void AWorkspaceOlderThanTheFieldIsRefusedWithItsOwnSentence()
        {
            BuildTargetsResponse older = Cdn();
            older.CdnSourceId = null;
            older.CdnSourceIdSpecified = false;
            Assert.That(FleetResolution.Resolve(1, Detail(), older, null).ReleaseProblem, Is.EqualTo(FleetResolution.NoCdnSourceFieldMessage));
        }

        [Test]
        public void AFleetWithNoDeploymentSaysSoUnderConnectButStillPushesToItsBranch()
        {
            FleetDetailResponse empty = Detail();
            empty.Deployments = new List<FleetDeploymentView>();
            FleetFacts facts = FleetResolution.Resolve(1, empty, null, null, Game());
            Assert.That(facts.Note, Is.EqualTo("Your fleet has no deployment, so no game servers are running. Add one in the panel."));
            Assert.That(facts.ReleaseProblem, Is.Null, "ReleaseGate has its own sentence for no deployment");
            Assert.That(facts.AppPublicId, Is.EqualTo("dscp_0123456789abcdef0123456789abcdef"), "the app id still reaches the client settings asset");

            // A regression: no deployment must not stop Push, which resolves from the game's branch.
            PushTarget target = PushTargetResolution.ForFleet(facts, 0);
            Assert.That((target.CanPush, target.CdnSourceId, target.Line), Is.EqualTo((true, 31L, "Pushes to CDN source #31.")), "[mutation: Push refuses a fleet with no deployment]");
            Assert.That(target.Line, Does.Not.Contain("deployment"));
        }

        [Test]
        public async Task ReadingAFleetReadsItsGameAndOnlyAFleetWithDeploymentsReadsItsBuildTargets()
        {
            var api = new FakePingCoreApi
            {
                GetFleet = id => ApiResult<FleetDetailResponse>.Success(Detail()),
                GetGameBranches = id => ApiResult<GameBranchesResponse>.Success(Game()),
                ListBuildTargets = id => ApiResult<BuildTargetsResponse>.Success(Cdn()),
            };
            (FleetFacts facts, PluginError error) = await ConnectState.Read(api, 1, CancellationToken.None);
            Assert.That(error, Is.Null);
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET fleets/1", "GET my-games/1", "GET fleets/1/build-targets" }));
            Assert.That(facts.Game.GameBranches.Single().GameBranchId, Is.EqualTo(4201));

            FleetDetailResponse empty = Detail();
            empty.Deployments = new List<FleetDeploymentView>();
            var none = new FakePingCoreApi
            {
                GetFleet = id => ApiResult<FleetDetailResponse>.Success(empty),
                GetGameBranches = id => ApiResult<GameBranchesResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.GetGame, PluginErrorKind.MissingBrandPermission, "Forbidden.", 403)),
            };
            (FleetFacts noDeployment, _) = await ConnectState.Read(none, 1, CancellationToken.None);
            Assert.That(none.Calls, Is.EqualTo(new[] { "GET fleets/1", "GET my-games/1" }), "a fleet with no deployment reads no build targets (they answer 400)");
            Assert.That(noDeployment.GameProblem, Does.StartWith("The fleet's game could not be read: Forbidden.").And.Contain("my-games.view"));
            Assert.That(PushTargetResolution.ForFleet(noDeployment, 0).Line, Is.EqualTo(noDeployment.GameProblem));
        }

        [Test]
        public void PickingAFleetOfAnotherGameForgetsTheBranchPushPushedToAndThePickedServerExecutable()
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingcore-connect-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(scratch);
            try
            {
                new EditorProjectSettings { GameId = 9001, FleetId = 42, GameBranchId = 4201, ProcessName = "./A.x86_64" }.Save(scratch);

                // No app id, so the client settings asset is never looked for: this writes the settings file alone.
                ConnectState.Write(scratch, new FleetFacts { FleetId = 43, GameId = 9001 });
                Assert.That(EditorProjectSettings.Load(scratch).GameBranchId, Is.EqualTo(4201), "another fleet of the same game keeps the branch");
                Assert.That(EditorProjectSettings.Load(scratch).ProcessName, Is.EqualTo("./A.x86_64"), "and the picked server executable");

                ConnectState.Write(scratch, new FleetFacts { FleetId = 9, GameId = 9003 });
                EditorProjectSettings other = EditorProjectSettings.Load(scratch);
                Assert.That((other.FleetId, other.GameId, other.GameBranchId), Is.EqualTo((9L, 9003L, 0L)), "[mutation: keep a branch of another game]");
                Assert.That(other.ProcessName, Is.Null, "[mutation: keep another game's server executable]");

                new EditorProjectSettings { GameId = 9003, FleetId = 9, GameBranchId = 500 }.Save(scratch);
                ConnectState.Write(scratch, new FleetFacts { FleetId = 10, GameId = 0 });
                Assert.That(EditorProjectSettings.Load(scratch).GameBranchId, Is.EqualTo(0), "a fleet with no game clears it too");
            }
            finally
            {
                System.IO.Directory.Delete(scratch, true);
            }
        }

        [Test]
        public void ABuildTargetsFailureCarriesTheWorkspacesOwnText()
        {
            var error = new PluginError("build-targets", PluginErrorKind.Rejected, "Could not resolve this fleet's deployment configuration.", null) { HttpStatus = 500 };
            FleetFacts facts = FleetResolution.Resolve(1, Detail(), null, error, Game());
            Assert.That(facts.ReleaseProblem, Is.EqualTo("The fleet's build targets could not be read: Could not resolve this fleet's deployment configuration. Press Refresh under Connect."));
            Assert.That(PushTargetResolution.ForFleet(facts, 0).CanPush, Is.True, "a build targets failure holds back Release only");
        }

        [Test]
        public void ThePickerNamesTheFleetItsGameAndItsDeployments()
        {
            FleetView fleet = WorkspaceFixtures.Payload<FleetListResponse>("fleets.list.json").Fleets.First();
            string choice = FleetResolution.Choice(fleet);
            Assert.That(choice, Does.StartWith(fleet.Name).And.Contain(fleet.GameName).And.EndWith("#" + fleet.FleetId));
            Assert.That(choice, Does.Contain((fleet.DeploymentCount ?? 0) == 1 ? "1 deployment" : (fleet.DeploymentCount ?? 0) + " deployments"));
            Assert.That(FleetResolution.Choice(new FleetView { FleetId = 9, Name = "Solo", GameId = 4, DeploymentCount = 1 }), Is.EqualTo("Solo (game #4, 1 deployment) #9"));
        }

        [Test]
        public void StatusRowsShowLocationGameServersStatusAndBuildVersion()
        {
            IReadOnlyList<DeploymentStatusRow> rows = DeploymentStatusRow.Of(Detail().Deployments, WorkspaceFixtures.Payload<FleetLiveResponse>("fleets.live.json"));
            Assert.That(rows.Single().ToString(), Is.EqualTo("Frankfurt: active; 3 game servers (2 ready, 1 in session, 0 draining); running 1.4.2"));
            Assert.That(rows.Single().DeploymentId, Is.EqualTo(100));
            Assert.That(DeploymentStatusRow.Of(Detail().Deployments, null).Single().GameServers, Is.EqualTo("3 game servers"), "the detail alone when the live read failed");
            Assert.That(DeploymentStatusRow.Of(new[] { new FleetDeploymentView { BrandDeploymentId = 5, LocationId = 2, ServerCount = 1 } }, null).Single().ToString(),
                Is.EqualTo("location #2: unknown; 1 game server; running no build reported yet"));
        }
    }
}
