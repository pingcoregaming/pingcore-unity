using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Tests.Fakes;
using PingCore.Editor.Workspace.UI.Ship;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// The startup command reads as a decision table: a fleet with deployments reads each member's spec, then the spec's
    /// template set (one process name, or several named); a fleet with none reads the game's template sets (one, several
    /// passing on any, none); a set that names no process skips; a 404 leaves that member or set out; any other refused read
    /// stops Push in the workspace's words. Never a branch's default deployment spec: no branch is even given.
    /// </summary>
    public sealed class StartupCommandReaderTests
    {
        private const long GameId = 9001;

        private static FleetDeploymentView Member(long id, string name) => new FleetDeploymentView { BrandDeploymentId = id, FriendlyName = name };

        private static GameTemplateSetView GameSet(long id, string name) => new GameTemplateSetView { TemplateSetId = id, SetName = name };

        private static TemplateSetResponse Set(long id, string name, string processName) => new TemplateSetResponse
        {
            TemplateSetId = id,
            SetName = name,
            Configs = new List<TemplateConfigView> { new TemplateConfigView { TemplateType = "cli", Active = true, ProcessName = processName } },
        };

        // A workspace where deployment 100 runs spec 5801 (set 2301), 101 runs spec 5802 (set 2302) and 102 runs spec 5801 too.
        private static FakePingCoreApi Api(Dictionary<long, string> processBySet, Dictionary<long, long> specByDeployment = null)
        {
            Dictionary<long, long> specs = specByDeployment ?? new Dictionary<long, long> { [100] = 5801, [101] = 5802, [102] = 5801 };
            return new FakePingCoreApi
            {
                GetDeployment = id => specs.TryGetValue(id, out long spec)
                    ? ApiResult<DeploymentReadResponse>.Success(new DeploymentReadResponse { Deployment = new DeploymentReadView { BrandDeploymentId = id, DeploymentSpecId = spec } })
                    : ApiResult<DeploymentReadResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.GetDeployment, PluginErrorKind.NotFound, "Deployment not found.", 404)),
                ListDeploymentSpecs = id => ApiResult<DeploymentSpecListResponse>.Success(new DeploymentSpecListResponse
                {
                    Specs = new List<DeploymentSpecView>
                    {
                        new DeploymentSpecView { SpecId = 5801, TemplateSetId = 2301, SpecName = "beacon-rush-linux" },
                        new DeploymentSpecView { SpecId = 5802, TemplateSetId = 2302, SpecName = "beacon-rush-beta" },
                        new DeploymentSpecView { SpecId = 60, TemplateSetId = null, SpecName = "no-set" },
                    },
                }),
                GetTemplateSet = (game, set) => processBySet.TryGetValue(set, out string process)
                    ? ApiResult<TemplateSetResponse>.Success(Set(set, set == 2301 ? "Linux" : set == 2302 ? "Beta" : "Other", process))
                    : ApiResult<TemplateSetResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.GetTemplateSet, PluginErrorKind.NotFound, "Template set not found", 404)),
            };
        }

        private static Task<StartupCheck> Read(FakePingCoreApi api, IReadOnlyList<FleetDeploymentView> deployments, IReadOnlyList<GameTemplateSetView> sets = null)
            => StartupCommandReader.ReadAsync(api, GameId, deployments, sets ?? new List<GameTemplateSetView>(), CancellationToken.None);

        [Test]
        public async Task DeploymentsWithOneProcessNameReadEachSpecThenItsTemplateSetOnce()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "./BeaconRushServer.x86_64 -batchmode" });
            StartupCheck check = await Read(api, new[] { Member(100, "eu-1"), Member(102, "us-1") }, new[] { GameSet(999, "Ignored") });

            Assert.That(check.Executable?.RelativePath, Is.EqualTo("BeaconRushServer.x86_64"));
            Assert.That(check.AnyOf, Is.False);
            Assert.That(api.Calls, Is.EqualTo(new[]
            {
                "GET brand/servers/deployments/100",
                "GET brand/servers/deployments/102",
                "GET my-games/9001/kubernetes/deployment-specs",
                "GET my-games/9001/template-sets/2301",
            }), "each member's spec, one spec list, each template set once; never the game's own sets while deployments exist [mutation: read the branch's default spec]");
        }

        [Test]
        public async Task DeploymentsWithSeveralProcessNamesRequireEachAndNameThem()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "./A.x86_64", [2302] = "./B.x86_64" });
            StartupCheck check = await Read(api, new[] { Member(100, "eu-1"), Member(101, "us-1") });

            Assert.That(check.Files.Paths, Is.EqualTo(new[] { "A.x86_64", "B.x86_64" }));
            Assert.That(check.AnyOf, Is.False, "[mutation: any one of the deployments' files]");
            Assert.That(check.Describe(), Does.Contain("./A.x86_64 (deployment eu-1), ./B.x86_64 (deployment us-1)"));
        }

        [Test]
        public async Task NoDeploymentAndOneTemplateSetChecksItsFile()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "./BeaconRushServer.x86_64" });
            StartupCheck check = await Read(api, new FleetDeploymentView[0], new[] { GameSet(2301, "Linux") });

            Assert.That(check.Executable?.RelativePath, Is.EqualTo("BeaconRushServer.x86_64"));
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET my-games/9001/template-sets/2301" }), "no deployment or spec read without deployments");
        }

        [Test]
        public async Task NoDeploymentAndSeveralTemplateSetsPassOnAnyOfTheirFiles()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "./A.x86_64", [2302] = "./B.x86_64" });
            StartupCheck check = await Read(api, null, new[] { GameSet(2301, "Linux"), GameSet(2302, "Beta") });

            Assert.That(check.AnyOf, Is.True, "[mutation: every template set's file]");
            Assert.That(check.Files.Paths, Is.EqualTo(new[] { "A.x86_64", "B.x86_64" }));
            Assert.That(StartupCommand.MissingProblem(check.Files, new[] { "B.x86_64" }), Is.Null);
            Assert.That(StartupCommand.MissingProblem(check.Files, new[] { "C.x86_64" }), Does.Contain("./A.x86_64, ./B.x86_64").And.Contain("none of them"), "refused only when none matches, naming them all");
            Assert.That(check.MatchedLine(new[] { "B.x86_64" }), Is.EqualTo("The build holds ./B.x86_64, which template set Beta launches."));
        }

        [Test]
        public async Task NoDeploymentAndNoTemplateSetSkipsWithoutACall()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string>());
            StartupCheck check = await Read(api, new FleetDeploymentView[0], new GameTemplateSetView[0]);

            Assert.That(check.Skipped, Is.EqualTo(StartupCheck.SkippedPrefix + "the fleet has no deployment and the game has no template set, so the file your game launches is not known."));
            Assert.That(check.Problem, Is.Null);
            Assert.That(api.Calls, Is.Empty);

            StartupCheck notRead = await StartupCommandReader.ReadAsync(api, GameId, null, null, CancellationToken.None);
            Assert.That(notRead.Skipped, Does.Contain("its game was not read"), "a game not read is not a game without sets");
        }

        [Test]
        public async Task ATemplateSetWithNoProcessNameSkipsWithTheReason()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "" });
            StartupCheck viaDeployment = await Read(api, new[] { Member(100, "eu-1") });
            Assert.That(viaDeployment.Skipped, Is.EqualTo(StartupCheck.SkippedPrefix + "no deployment of the fleet names a process to launch (deployment eu-1: template set Linux names no process to launch (no active command-line config with a process name))."));

            StartupCheck viaGame = await Read(Api(new Dictionary<long, string> { [2301] = "  " }), null, new[] { GameSet(2301, "Linux") });
            Assert.That(viaGame.Skipped, Does.Contain("template set Linux names no process to launch"));
            Assert.That((viaDeployment.Problem, viaGame.Problem), Is.EqualTo(((string)null, (string)null)), "[mutation: refuse the push on a missing process name]");
        }

        [Test]
        public async Task A404LeavesThatDeploymentOrTemplateSetOutAndNoneLeftSkips()
        {
            // Deployment 103 is gone; 100's set 2301 answers. The gone member is named, the other still checked.
            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "./A.x86_64" });
            StartupCheck partial = await Read(api, new[] { Member(100, "eu-1"), Member(103, "old") });
            Assert.That(partial.Executable?.RelativePath, Is.EqualTo("A.x86_64"));
            Assert.That(partial.Note, Is.EqualTo("Not checked: deployment old: it no longer exists."));

            // Spec 5802's set 2302 is gone: the only member is left out, so the check is skipped, never refused.
            StartupCheck setGone = await Read(Api(new Dictionary<long, string>()), new[] { Member(101, "us-1") });
            Assert.That(setGone.Skipped, Does.Contain("deployment us-1: its template set #2302 no longer exists"));
            Assert.That(setGone.Problem, Is.Null);

            StartupCheck gameSetGone = await Read(Api(new Dictionary<long, string> { [2302] = "./B.x86_64" }), null, new[] { GameSet(2301, "Linux"), GameSet(2302, "Beta") });
            Assert.That(gameSetGone.Executable?.RelativePath, Is.EqualTo("B.x86_64"));
            Assert.That(gameSetGone.Note, Is.EqualTo("Not checked: template set Linux no longer exists."));
        }

        [Test]
        public async Task AMemberListedTwiceIsReadOnce()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "./A.x86_64" });
            StartupCheck check = await Read(api, new[] { Member(100, "eu-1"), Member(100, "eu-1") });
            Assert.That(check.Executable?.RelativePath, Is.EqualTo("A.x86_64"));
            Assert.That(api.Calls.Count(c => c == "GET brand/servers/deployments/100"), Is.EqualTo(1), "[mutation: one read per row] the deployment read is rate limited");
        }

        [Test]
        public async Task AMemberWithNoSpecOrASpecWithNoTemplateSetIsNamed()
        {
            FakePingCoreApi api = Api(new Dictionary<long, string>(), new Dictionary<long, long> { [100] = 0, [101] = 60, [102] = 77 });
            StartupCheck check = await Read(api, new[] { Member(100, "eu-1"), Member(101, "us-1"), Member(102, "ap-1") });
            Assert.That(check.Skipped, Does.Contain("deployment eu-1: it names no deployment spec")
                .And.Contain("deployment us-1: its deployment spec no-set names no template set")
                .And.Contain("deployment ap-1: its deployment spec #77 is not among the game's specs"));
        }

        [Test]
        public async Task ARefusedReadStopsPushInTheWorkspacesWordsNamingTheBrandPermission()
        {
            FakePingCoreApi deploymentRefused = Api(new Dictionary<long, string>());
            deploymentRefused.GetDeployment = id => ApiResult<DeploymentReadResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.GetDeployment, PluginErrorKind.MissingBrandPermission, "Forbidden.", 403));
            StartupCheck stopped = await Read(deploymentRefused, new[] { Member(100, "eu-1") });
            Assert.That(stopped.Problem, Does.StartWith("The startup command could not be read: Forbidden.").And.Contain("brand.servers.view"));
            Assert.That(stopped.Skipped, Is.Null, "[mutation: skip on every failure]");

            FakePingCoreApi specsRefused = Api(new Dictionary<long, string>());
            specsRefused.ListDeploymentSpecs = id => ApiResult<DeploymentSpecListResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.ListDeploymentSpecs, PluginErrorKind.MissingBrandPermission, "Forbidden.", 403));
            Assert.That((await Read(specsRefused, new[] { Member(100, "eu-1") })).Problem, Does.Contain("my-games.view"));

            FakePingCoreApi setRefused = Api(new Dictionary<long, string>());
            setRefused.GetTemplateSet = (game, set) => ApiResult<TemplateSetResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.GetTemplateSet, PluginErrorKind.MissingBrandPermission, "Forbidden.", 403));
            Assert.That((await Read(setRefused, null, new[] { GameSet(2301, "Linux") })).Problem, Does.Contain("my-games.templates"));
        }

        [Test]
        public async Task TheTargetCarriesTheFleetsDeploymentsAndTheGamesTemplateSets()
        {
            var facts = new PingCore.Editor.Workspace.UI.Connect.FleetFacts
            {
                FleetId = 42,
                GameId = GameId,
                Deployments = new[] { Member(100, "eu-1") },
                Game = new GameBranchesResponse
                {
                    GameId = GameId,
                    GameBranches = new List<GameBranchView> { new GameBranchView { GameBranchId = 4201, DataSourceType = "cdn_source", CdnSourceId = 31 } },
                    TemplateSets = new List<GameTemplateSetView> { GameSet(2301, "Linux") },
                },
            };
            PushTarget target = PushTargetResolution.ForFleet(facts, 0);
            Assert.That(target.Deployments.Single().BrandDeploymentId, Is.EqualTo(100));
            Assert.That(target.TemplateSets.Single().TemplateSetId, Is.EqualTo(2301));

            FakePingCoreApi api = Api(new Dictionary<long, string> { [2301] = "./A.x86_64" });
            StartupCheck check = await StartupCommandReader.ReadAsync(api, target, CancellationToken.None);
            Assert.That(check.Executable?.RelativePath, Is.EqualTo("A.x86_64"));
            Assert.That(api.Calls.First(), Is.EqualTo("GET brand/servers/deployments/100"), "the deployments decide while there are any");
        }
    }
}
