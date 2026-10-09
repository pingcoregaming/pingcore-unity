using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Tests.Fakes;
using PingCore.Editor.Workspace.UI.Connect;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// When a focus of the window reads the picked fleet again, so a deployment added in the panel reaches the chips, and
    /// that an answer for a fleet that is no longer the picked one is dropped, so an older read never undoes a pick.
    /// </summary>
    public sealed class ConnectStateTests
    {
        private static readonly DateTime Read = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        [TestCase(null, 0, false, TestName = "never read: the window's load reads, not a focus")]
        [TestCase(0, 59, false, TestName = "read 59 s ago: not again")]
        [TestCase(0, 60, true, TestName = "read a minute ago: again")]
        [TestCase(0, 3600, true, TestName = "read an hour ago: again")]
        [TestCase(0, -30, false, TestName = "a clock that went backwards: not again")]
        public void AFocusReadsTheFleetAgainOnlyOnceItsFactsAreAMinuteOld(int? readAt, int secondsLater, bool again)
        {
            DateTime? last = readAt.HasValue ? Read.AddSeconds(readAt.Value) : (DateTime?)null;
            Assert.That(ConnectState.ShouldReadAgain(last, Read.AddSeconds(secondsLater)), Is.EqualTo(again));
        }

        private static FakePingCoreApi Api() => new FakePingCoreApi
        {
            GetFleet = id => ApiResult<FleetDetailResponse>.Success(WorkspaceFixtures.Payload<FleetDetailResponse>("fleets.get.json")),
            GetGameBranches = id => ApiResult<GameBranchesResponse>.Success(WorkspaceFixtures.Payload<GameBranchesResponse>("game.get.json")),
            ListBuildTargets = id => ApiResult<BuildTargetsResponse>.Success(WorkspaceFixtures.Payload<BuildTargetsResponse>("build-targets.cdn.json")),
        };

        [Test]
        public async Task AReadWhoseFleetIsNoLongerPickedIsDroppedAndChangesNothing()
        {
            var state = new ConnectState();
            var picked = new FleetFacts { FleetId = 9, FleetName = "Picked meanwhile" };
            state.Use(picked);
            DateTime? before = state.LastReadUtc;

            FakePingCoreApi api = Api();
            await state.ResolveAsync(api, 1, CancellationToken.None, () => false);
            Assert.That(api.Calls, Is.Not.Empty, "the read itself happened");
            Assert.That((state.Facts, state.Problem, state.LastReadUtc), Is.EqualTo((picked, (string)null, before)),
                "[mutation: drop the stillPicked check] the older read must not replace the fleet picked meanwhile");

            await state.ResolveAsync(Api(), 1, CancellationToken.None, () => true);
            Assert.That(state.Facts, Is.Not.SameAs(picked), "the control: a read of the still-picked fleet is kept");
            Assert.That(state.Facts.FleetId, Is.Not.EqualTo(9L));
        }
    }
}
