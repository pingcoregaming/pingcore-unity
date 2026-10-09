using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.Ship;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// Push resolves from the fleet's game: fleet, then game, then the branch it pushes to and that branch's data source,
    /// as a table over one branch of every kind, several branches, and the remembered choice. Never from deployments.
    /// </summary>
    public sealed class PushTargetTests
    {
        private static GameBranchView Branch(long id, string name, string dataSource, long? cdnSourceId = null, string platform = "linux")
            => new GameBranchView { GameBranchId = id, BranchName = name, DataSourceType = dataSource, CdnSourceId = cdnSourceId, Platform = platform };

        private static GameBranchesResponse Game(params GameBranchView[] branches) => new GameBranchesResponse { GameId = 9001, Name = "Beacon Rush", GameBranches = new List<GameBranchView>(branches) };

        public static IEnumerable<TestCaseData> OneBranch()
        {
            yield return new TestCaseData("none", null, PushTargetKind.NoDataSource,
                "Branch Main has no data source, so there is nowhere to push. Set its data source to a CDN source in the panel.").SetName("Push to branch: no data source says there is nowhere to push");
            yield return new TestCaseData(null, null, PushTargetKind.NoDataSource,
                "Branch Main has no data source, so there is nowhere to push. Set its data source to a CDN source in the panel.").SetName("Push to branch: a missing data source reads as none");
            yield return new TestCaseData("image", null, PushTargetKind.Image, DataSourceRule.ImageMessage).SetName("Push to branch: an image branch gets the spec's image sentence");
            yield return new TestCaseData("steam_depot", null, PushTargetKind.Steam,
                "Branch Main gets its files from Steam, and the plugin pushes to CDN sources only.").SetName("Push to branch: a Steam branch is outside the plugin");
            yield return new TestCaseData("cdn_source", 31L, PushTargetKind.Cdn, "Pushes to CDN source #31.").SetName("Push to branch: a CDN branch with its source pushes there");
            yield return new TestCaseData("cdn_source", null, PushTargetKind.CdnWithoutSource,
                "Branch Main delivers from a CDN source but names none, so there is nowhere to push. Pick its CDN source in the panel.").SetName("Push to branch: a CDN branch without a source id refuses");
            yield return new TestCaseData("cdn_source", 0L, PushTargetKind.CdnWithoutSource,
                "Branch Main delivers from a CDN source but names none, so there is nowhere to push. Pick its CDN source in the panel.").SetName("Push to branch: a CDN branch whose source id is 0 refuses");
            yield return new TestCaseData("cdn_legacy", null, PushTargetKind.LegacyCdn,
                "Branch Main uses the legacy CDN, which the plugin does not push to. Set its data source to a CDN source in the panel.").SetName("Push to branch: the legacy CDN is named");
            yield return new TestCaseData("ftp", null, PushTargetKind.Other,
                "Branch Main gets its files from 'ftp', and the plugin pushes to CDN sources only. Set its data source to a CDN source in the panel.").SetName("Push to branch: an unknown data source is named");
        }

        [TestCaseSource(nameof(OneBranch))]
        public void TheOnlyBranchIsTakenAndItsDataSourceDecides(string dataSource, long? cdnSourceId, PushTargetKind kind, string line)
        {
            PushTarget target = PushTargetResolution.Resolve(Game(Branch(4201, "Main", dataSource, cdnSourceId)), 0);
            Assert.That(target.Branch?.GameBranchId, Is.EqualTo(4201), "the game's only branch needs no pick");
            Assert.That((target.Kind, target.Line), Is.EqualTo((kind, line)));
            Assert.That(target.CanPush, Is.EqualTo(kind == PushTargetKind.Cdn));
            Assert.That(target.CdnSourceId, Is.EqualTo(kind == PushTargetKind.Cdn ? 31L : 0L), "[mutation: a source id on a branch that cannot push]");
            Assert.That(target.Problem, Is.EqualTo(kind == PushTargetKind.Cdn ? null : line));
            Assert.That(target.OutsideThePlugin, Is.EqualTo(kind == PushTargetKind.Image || kind == PushTargetKind.Steam));
            Assert.That(target.Line, Does.Not.Contain("deployment"), "Push never mentions deployments");
        }

        [Test]
        public void SeveralBranchesWaitForAPickAndTheRememberedOneIsTaken()
        {
            GameBranchesResponse game = Game(Branch(4201, "Linux", "cdn_source", 31), Branch(4202, "Windows", "steam_depot", platform: "windows"), Branch(4203, "Beta", "cdn_source", 32));
            PushTarget unpicked = PushTargetResolution.Resolve(game, 0);
            Assert.That((unpicked.Kind, unpicked.Branch, unpicked.CanPush), Is.EqualTo((PushTargetKind.PickBranch, (GameBranchView)null, false)), "[mutation: take the first or the default branch]");
            Assert.That(unpicked.Line, Is.EqualTo(PushTargetResolution.PickBranchMessage));
            Assert.That(unpicked.Branches.Select(b => b.GameBranchId), Is.EqualTo(new[] { 4201L, 4202L, 4203L }));

            PushTarget beta = PushTargetResolution.Resolve(game, 4203);
            Assert.That((beta.Kind, beta.CdnSourceId, beta.Branch.BranchName), Is.EqualTo((PushTargetKind.Cdn, 32L, "Beta")));
            Assert.That(PushTargetResolution.Resolve(game, 4202).Kind, Is.EqualTo(PushTargetKind.Steam));

            PushTarget stale = PushTargetResolution.Resolve(game, 999);
            Assert.That(stale.Kind, Is.EqualTo(PushTargetKind.PickBranch), "a branch of another game, or one deleted, asks again");
            Assert.That(PushTargetResolution.Resolve(Game(Branch(4201, "Linux", "cdn_source", 31)), 999).Branch.GameBranchId, Is.EqualTo(4201), "the only branch wins over a stale id");
        }

        [Test]
        public void NoBranchANotReadGameAndAFleetWithNoGameSaySo()
        {
            Assert.That(PushTargetResolution.Resolve(Game(), 0).Kind, Is.EqualTo(PushTargetKind.NoBranches));
            Assert.That(PushTargetResolution.Resolve(Game(), 0).Line, Is.EqualTo(PushTargetResolution.NoBranchesMessage));
            Assert.That(PushTargetResolution.Resolve(null, 4201).Kind, Is.EqualTo(PushTargetKind.NotRead));
            Assert.That(PushTargetResolution.ForFleet(null, 4201).Line, Is.EqualTo(PushTargetResolution.NotReadMessage));
            Assert.That(PushTargetResolution.ForFleet(new FleetFacts { FleetId = 1 }, 0).Line, Is.EqualTo(PushTargetResolution.NoGameMessage));
            Assert.That(PushTargetResolution.ForFleet(new FleetFacts { FleetId = 1, GameId = 9001, GameProblem = "The fleet's game could not be read: Forbidden." }, 0).Line,
                Is.EqualTo("The fleet's game could not be read: Forbidden."));
        }

        [Test]
        public void ThePickerNamesEachBranchItsPlatformAndWhereItsFilesComeFrom()
        {
            Assert.That(PushTargetResolution.Choice(Branch(4201, "Linux", "cdn_source", 31)), Is.EqualTo("Linux (linux, CDN source #31)"));
            Assert.That(PushTargetResolution.Choice(Branch(4202, "Win", "steam_depot", platform: "windows")), Is.EqualTo("Win (windows, Steam)"));
            Assert.That(PushTargetResolution.Choice(Branch(4203, " ", "none", platform: null)), Is.EqualTo("#4203 (no platform, no data source)"));
            Assert.That(PushTargetResolution.Choice(Branch(415, "Img", "image")), Is.EqualTo("Img (linux, container image)"));
            Assert.That(PushTargetResolution.Choice(Branch(416, "Cdn", "cdn_source")), Is.EqualTo("Cdn (linux, CDN source not set)"));
        }

        [Test]
        public void ThePickerAddsTheIdOnlyToEntriesThatWouldReadTheSame()
        {
            var branches = new List<GameBranchView> { Branch(4201, "Main", "cdn_source", 31), Branch(4202, "Main", "cdn_source", 31), Branch(4203, "Beta", "cdn_source", 31) };
            Assert.That(PushTargetResolution.Choices(branches), Is.EqualTo(new[]
            {
                "Main (linux, CDN source #31) #4201",
                "Main (linux, CDN source #31) #4202",
                "Beta (linux, CDN source #31)",
            }), "[mutation: drop the id everywhere] two entries reading the same would pick the wrong branch");
            Assert.That(PushTargetResolution.Choices(null), Is.Empty);
        }

        [TestCase("Main", "branch-4301", "Main", TestName = "the panel's name (branchDescription) wins over the legacy branch-<id>")]
        [TestCase(" Main ", "branch-4301", "Main", TestName = "the name is trimmed")]
        [TestCase("", "branch-4301", "branch-4301", TestName = "no description falls back to branchName")]
        [TestCase(null, " ", "#4301", TestName = "neither falls back to the id")]
        public void ABranchIsNamedAsThePanelNamesIt(string description, string branchName, string expected)
        {
            var branch = new GameBranchView { GameBranchId = 4301, BranchName = branchName, BranchDescription = description, DataSourceType = "cdn_source", CdnSourceId = 3002, Platform = "linux" };
            Assert.That(PushTargetResolution.Name(branch), Is.EqualTo(expected));
            Assert.That(branch.DisplayName(), Is.EqualTo(expected));
        }

        [Test]
        public void ABranchNotForLinuxWarnsButStillPushes()
        {
            PushTarget windows = PushTargetResolution.Resolve(Game(Branch(4201, "Win", "cdn_source", 31, platform: "windows")), 0);
            Assert.That(windows.CanPush, Is.True, "a warning, not a refusal");
            Assert.That(windows.Warning, Is.EqualTo("Branch Win is for windows, but Ship builds a Linux dedicated server; check the branch's platform in the panel."));
            Assert.That(PushTargetResolution.Resolve(Game(Branch(4201, "Lin", "cdn_source", 31, platform: "Linux")), 0).Warning, Is.Null);
            Assert.That(PushTargetResolution.Resolve(Game(Branch(4201, "Any", "cdn_source", 31, platform: null)), 0).Warning, Is.Null);
        }

        [Test]
        public void ThePinnedGameFixtureResolvesToItsCdnSource()
        {
            PushTarget target = PushTargetResolution.Resolve(WorkspaceFixtures.Payload<GameBranchesResponse>("game.get.json"), 0);
            Assert.That((target.Kind, target.GameId, target.Branch.GameBranchId, target.CdnSourceId), Is.EqualTo((PushTargetKind.Cdn, 9001L, 4201L, 31L)));
        }

        [Test]
        public void ThePinnedGameFixtureMapsTheBranchNameThePanelShows()
        {
            // The raw answer carries both keys: a choice reading "branch-4201 (linux, CDN source #31)" would not match the panel's "Main".
            GameBranchView branch = WorkspaceFixtures.Payload<GameBranchesResponse>("game.get.json").GameBranches.Single();
            Assert.That((branch.BranchName, branch.BranchDescription), Is.EqualTo(("branch-4201", "Main")), "[mutation: map branchDescription to the wrong key]");
            Assert.That(PushTargetResolution.Choice(branch), Is.EqualTo("Main (linux, CDN source #31)"));
            branch.DataSourceType = "none";
            Assert.That(PushTargetResolution.Resolve(new GameBranchesResponse { GameId = 9001, GameBranches = new List<GameBranchView> { branch } }, 0).Line,
                Does.StartWith("Branch Main has no data source"), "every message names the branch as the panel does");
        }
    }
}
