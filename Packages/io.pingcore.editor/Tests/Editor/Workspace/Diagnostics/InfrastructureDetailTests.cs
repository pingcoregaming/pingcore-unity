using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Discovery.Client;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Infrastructure;
using PingCore.Editor.Workspace.Tests.Fakes;

namespace PingCore.Editor.Workspace.Tests.Infrastructure
{
    /// <summary>
    /// The Editor's exact reason behind a game client's missing-infrastructure message, from the pinned fleet fixtures:
    /// no deployment, a deployment starting, failed, scaled to zero or ready, the app id that is not the fleet's, and a
    /// failed read giving no detail at all. Then the source: two GETs, never a write, null on any failure, nothing
    /// credential-shaped in what it answers.
    /// </summary>
    public sealed class InfrastructureDetailTests
    {
        private const string FleetApp = "dscp_0123456789abcdef0123456789abcdef";

        private static FleetDetailResponse Fleet() => WorkspaceFixtures.Payload<FleetDetailResponse>("fleets.get.json");

        private static FleetLiveResponse Live() => WorkspaceFixtures.Payload<FleetLiveResponse>("fleets.live.json");

        private static FleetLiveResponse NoneReady()
        {
            FleetLiveResponse live = Live();
            foreach (FleetLiveDeploymentView d in live.Deployments)
            {
                d.Total = 1;
                d.Ready = 0;
                d.InSession = 0;
                d.Draining = 0;
            }

            return live;
        }

        private static FleetDetailResponse WithStatus(string status, int serverCount = 0, int minServers = 1)
        {
            FleetDetailResponse fleet = Fleet();
            fleet.Deployments[0].DeploymentStatus = status;
            fleet.Deployments[0].ServerCount = serverCount;
            fleet.Deployments[0].MinServers = minServers;
            return fleet;
        }

        [Test]
        public void TheFixturesAreTheFleetTheseCasesStartFrom()
        {
            FleetDetailResponse fleet = Fleet();
            Assert.That(fleet.Fleet.Name, Is.EqualTo("EU Matchmaking"));
            Assert.That(fleet.Fleet.DiscoveryPublicId, Is.EqualTo(FleetApp));
            Assert.That(fleet.Deployments.Count, Is.EqualTo(1));
            Assert.That(Live().Deployments[0].BrandDeploymentId, Is.EqualTo(fleet.Deployments[0].BrandDeploymentId));
        }

        [Test]
        public void NoDeploymentNamesTheFleet()
        {
            FleetDetailResponse fleet = Fleet();
            fleet.Deployments = new List<FleetDeploymentView>();
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, fleet, Live()), Is.EqualTo("Fleet EU Matchmaking has no deployment."));
            fleet.Deployments = null;
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, fleet, Live()), Is.EqualTo("Fleet EU Matchmaking has no deployment."), "absent is none");
        }

        [TestCase("deploying")]
        [TestCase("pending")]
        [TestCase("scaling")]
        public void AStartingDeploymentSaysHowManyAreReady(string status)
        {
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, WithStatus(status), NoneReady()),
                Is.EqualTo("Fleet EU Matchmaking: its deployment is starting (0 of 1 ready)."));
        }

        [Test]
        public void AFailedDeploymentIsNamed()
        {
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, WithStatus("failed"), NoneReady()),
                Is.EqualTo("Deployment eu-1 of Fleet EU Matchmaking failed."));
            FleetDetailResponse unnamed = WithStatus("failed");
            unnamed.Deployments[0].FriendlyName = null;
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, unnamed, NoneReady()), Does.StartWith("Deployment Frankfurt of"), "the location stands in for a missing name");
        }

        [Test]
        public void ReadyGameServersSayDiscoveryWillListThem()
        {
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, Fleet(), Live()),
                Is.EqualTo("Fleet EU Matchmaking has 2 game servers ready; Discovery lists a game server a few seconds after it calls ready. Check again."));
        }

        [Test]
        public void ARunningDeploymentWithNothingReadySaysSoAndCountsDraining()
        {
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, WithStatus("active", 1), NoneReady()),
                Is.EqualTo("Fleet EU Matchmaking: its deployment is running, but no game server is ready yet (0 of 1 ready). Ship a build if none has been released."));
            FleetLiveResponse draining = NoneReady();
            draining.Deployments[0].Draining = 1;
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, WithStatus("active", 1), draining),
                Does.Contain("(0 of 1 ready, 1 draining)"));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, WithStatus("active", 0, 0), NoneReadyAtZero()),
                Is.EqualTo("Fleet EU Matchmaking: its deployment is scaled to 0 game servers. Raise the minimum in the panel."));
        }

        [Test]
        public void AnAppIdThatIsNotTheFleetsIsSaidForEveryState()
        {
            const string other = "dscp_ffffffffffffffffffffffffffffffff";
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.AppUnknown, other, Fleet(), Live()),
                Is.EqualTo("This game's app id is not the Discovery app of Fleet EU Matchmaking (" + FleetApp + "). Pick the fleet again in Window > PingCore, Connect."));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, other, Fleet(), Live()), Does.StartWith("This game asks another Discovery app"));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.AppUnknown, FleetApp, Fleet(), Live()), Does.Contain("is unknown to Discovery or disabled"));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoAppId, null, Fleet(), Live()), Does.StartWith("Fleet EU Matchmaking is picked in Window > PingCore, but the client settings asset this game reads has no app id."));
        }

        [Test]
        public void ReadyGameServersWinOverAFailedSibling()
        {
            FleetDetailResponse two = Fleet();
            two.Deployments.Add(new FleetDeploymentView { BrandDeploymentId = 101, FriendlyName = "eu-2", DeploymentStatus = "failed", LocationName = "Paris" });
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, two, Live()), Does.StartWith("Fleet EU Matchmaking has 2 game servers ready"), "Discovery only lags behind the ready ones");
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, two, NoneReady()), Is.EqualTo("Deployment eu-2 of Fleet EU Matchmaking failed."), "control: with none ready the failure is named");
        }

        [Test]
        public void WithoutTheLiveStateTheFleetReadAloneStillAnswers()
        {
            // GET fleets/{id}/live answers 503 while the fleet's app is disabled or has no active token.
            FleetDetailResponse none = Fleet();
            none.Deployments = new List<FleetDeploymentView>();
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, none, null), Is.EqualTo("Fleet EU Matchmaking has no deployment."));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, WithStatus("failed"), null), Is.EqualTo("Deployment eu-1 of Fleet EU Matchmaking failed."));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, WithStatus("deploying"), null), Is.EqualTo("Fleet EU Matchmaking: its deployment is starting."));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, Fleet(), null),
                Is.EqualTo("Fleet EU Matchmaking: its deployment is running (3 game servers), but its live state could not be read: the fleet's Discovery app may be disabled or have no active token. Check the app on the panel's Discovery page."));
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.AppUnknown, FleetApp, Fleet(), null), Does.Contain("is unknown to Discovery or disabled"), "a disabled app is said without the live state");
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoAppId, null, Fleet(), null), Does.StartWith("Fleet EU Matchmaking is picked"));
        }

        [Test]
        public void AFailedFleetReadOrAnUnexplainedStateGivesNoDetail()
        {
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, null, Live()), Is.Null, "the fleet read failed");
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.NoGameServers, FleetApp, new FleetDetailResponse(), Live()), Is.Null, "no fleet in the answer");
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.Ok, FleetApp, Fleet(), Live()), Is.Null);
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.Unreachable, FleetApp, Fleet(), Live()), Is.Null);
            Assert.That(InfrastructureDetail.Describe(InfrastructureState.DiscoveryError, FleetApp, Fleet(), Live()), Is.Null);
        }

        [Test]
        public async Task TheSourceReadsTheFleetAndItsLiveStateOnlyAndAnswersTheDetail()
        {
            var api = new FakePingCoreApi
            {
                GetFleet = id => ApiResult<FleetDetailResponse>.Success(WithStatus("deploying")),
                GetFleetLive = id => ApiResult<FleetLiveResponse>.Success(NoneReady()),
            };
            string detail = await InfrastructureDetailSource.AnswerAsync(api, 1, Question(InfrastructureState.NoGameServers, FleetApp), CancellationToken.None);
            Assert.That(detail, Is.EqualTo("Fleet EU Matchmaking: its deployment is starting (0 of 1 ready)."));
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET fleets/1", "GET fleets/1/live" }), "two reads, nothing else");

            api.Calls.Clear();
            Assert.That(await InfrastructureDetailSource.AnswerAsync(api, 1, Question(InfrastructureState.AppUnknown, FleetApp), CancellationToken.None), Does.Contain("unknown to Discovery or disabled"));
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET fleets/1" }), "only no game servers needs the live state");
        }

        [Test]
        public void TheEditorPluginRegistersItsAnswererWhenTheEditorLoads()
        {
            // InfrastructureDetailHook is [InitializeOnLoad]; without it the game's banner never gets the exact reason.
            Assert.That(InfrastructureEditorHook.IsRegistered, Is.True);
        }

        [Test]
        public async Task ASourceReadThatFailsAnswersNullAndNeverThrows()
        {
            var unscripted = new FakePingCoreApi();
            Assert.That(await InfrastructureDetailSource.AnswerAsync(unscripted, 1, Question(InfrastructureState.NoGameServers, FleetApp), CancellationToken.None), Is.Null, "the fleet read failed");

            var noLive = new FakePingCoreApi { GetFleet = id => ApiResult<FleetDetailResponse>.Success(WithStatus("failed")) };
            Assert.That(await InfrastructureDetailSource.AnswerAsync(noLive, 1, Question(InfrastructureState.NoGameServers, FleetApp), CancellationToken.None),
                Is.EqualTo("Deployment eu-1 of Fleet EU Matchmaking failed."), "a failed live read (unscripted here) only leaves the counts out");
            Assert.That(noLive.Calls, Is.EqualTo(new[] { "GET fleets/1", "GET fleets/1/live" }));

            var throwing = new FakePingCoreApi { GetFleet = id => throw new InvalidOperationException("boom") };
            Assert.That(await InfrastructureDetailSource.AnswerAsync(throwing, 1, Question(InfrastructureState.NoGameServers, FleetApp), CancellationToken.None), Is.Null);

            Assert.That(await InfrastructureDetailSource.AnswerAsync(new FakePingCoreApi(), 0, Question(InfrastructureState.NoGameServers, FleetApp), CancellationToken.None), Is.Null, "no fleet picked");
            Assert.That(await InfrastructureDetailSource.AnswerAsync(null, 1, Question(InfrastructureState.NoGameServers, FleetApp), CancellationToken.None), Is.Null, "not signed in");
        }

        [Test]
        public async Task TheAnswerNeverCarriesAnythingShapedLikeACredential()
        {
            string token = "usr" + "_" + "AbCdEfGh12345678";
            FleetDetailResponse named = Fleet();
            named.Fleet.Name = "Fleet of " + token;
            named.Deployments = new List<FleetDeploymentView>();
            var api = new FakePingCoreApi
            {
                GetFleet = id => ApiResult<FleetDetailResponse>.Success(named),
                GetFleetLive = id => ApiResult<FleetLiveResponse>.Success(Live()),
            };
            string detail = await InfrastructureDetailSource.AnswerAsync(api, 1, Question(InfrastructureState.NoGameServers, FleetApp), CancellationToken.None);
            Assert.That(detail, Does.Contain("has no deployment."), "control: the sentence is there");
            Assert.That(detail, Does.Not.Contain(token));
        }

        private static FleetLiveResponse NoneReadyAtZero()
        {
            FleetLiveResponse live = NoneReady();
            live.Deployments[0].Total = 0;
            return live;
        }

        private static InfrastructureQuestion Question(InfrastructureState state, string appPublicId) => new InfrastructureQuestion(state, appPublicId, InfrastructureCheck.MessageFor(state));
    }
}
