using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using static PingCore.Editor.Workspace.Tests.Pipeline.PipelineFixtures;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>How release polls read: states, per-location progress, refusal sentences.</summary>
    public sealed class ReleaseWatcherTests
    {
        [TestCase("pending", null, ReleaseVerdict.InProgress)]
        [TestCase("surging", null, ReleaseVerdict.InProgress)]
        [TestCase("rolling", null, ReleaseVerdict.InProgress)]
        [TestCase("completing", null, ReleaseVerdict.InProgress)]
        [TestCase("a_state_from_the_future", null, ReleaseVerdict.InProgress)]
        [TestCase("completed", null, ReleaseVerdict.Completed)]
        [TestCase("failed", null, ReleaseVerdict.FailedUnacknowledged)]
        [TestCase("failed", "", ReleaseVerdict.FailedUnacknowledged)]
        [TestCase("failed", "2026-10-07 10:00:00", ReleaseVerdict.FailedAcknowledged)]
        [TestCase("rolled_back", null, ReleaseVerdict.Ended)]
        [TestCase("dismissed", null, ReleaseVerdict.Ended)]
        public void EveryReleaseStateReadsAsItShould(string state, string acknowledgedAt, ReleaseVerdict verdict)
        {
            Assert.That(ReleaseWatcher.Classify(new ReleaseView { State = state, AcknowledgedAt = acknowledgedAt }), Is.EqualTo(verdict));
        }

        [Test]
        public void OnlyTheFourEndStatesAreFinal()
        {
            Assert.That(new[] { "completed", "failed", "rolled_back", "dismissed" }.All(ReleaseWatcher.IsFinal), Is.True);
            Assert.That(new[] { "pending", "surging", "rolling", "completing", null }.Any(ReleaseWatcher.IsFinal), Is.False);
        }

        [Test]
        public void PerLocationProgressIsReadFromEveryMemberDeployment()
        {
            ReleaseDetailResponse detail = Detail(12, "rolling", null, null, Location(21, "rolling", 1, 1, 0), Location(22, "pending", 2, 0, 0, "waiting for a free surge slot"));
            var rows = ReleaseWatcher.Progress(detail);
            Assert.That(rows.Select(r => r.ToString()), Is.EqualTo(new[]
            {
                "deployment 21: rolling, 1 old, 1 new, 0 retiring",
                "deployment 22: pending, 2 old, 0 new, 0 retiring (blocked: waiting for a free surge slot)",
            }));
            Assert.That(ReleaseWatcher.Describe(12, "rolling", "maintenance window", rows), Does.StartWith("release 12: rolling (waiting: maintenance window); deployment 21"));
            Assert.That(ReleaseWatcher.Progress(null), Is.Empty);
        }

        [Test]
        public void OnlyServerUnreachableIsRetried()
        {
            foreach (string reason in new[] { "release_in_progress", "snapshot_not_found", "cdn_unreadable", "too_many_pins", "supervisor_too_old", null, "x" })
            {
                Assert.That(ReleaseWatcher.Refusal(reason).Retry, Is.False, reason);
                Assert.That(ReleaseWatcher.Refusal(reason).Message, Is.Not.Empty);
            }

            Assert.That(ReleaseWatcher.Refusal("server_unreachable").Retry, Is.True);
            Assert.That(ReleaseWatcher.Refusal("release_in_progress").Message, Does.Contain("cancel"));
        }
    }
}
