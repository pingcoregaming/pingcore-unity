using System;
using NUnit.Framework;
using PingCore.Fleet.Sessions;

namespace PingCore.Fleet.Tests.Editor.Sessions
{
    /// <summary>The pure joinable policy: the open-seat clamp, the republish cadence, withdraw at 0, the retry pause.</summary>
    public sealed class JoinablePlanTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        [TestCase(8, 2, 1, null, null, 5, TestName = "no players counter: maxPlayers - connected - expected")]
        [TestCase(8, 2, 1, 8L, 2L, 5, TestName = "a counter with more free capacity does not clamp")]
        [TestCase(20, 2, 0, 8L, 2L, 6, TestName = "maxPlayers above the players capacity is clamped to the free capacity")]
        [TestCase(8, 2, 0, 8L, 7L, 1, TestName = "a counter the game wrote ahead of connections clamps lower")]
        [TestCase(8, 8, 0, 8L, 8L, 0, TestName = "a full session has no open seats")]
        [TestCase(8, 6, 4, null, null, 0, TestName = "expected joiners past the limit never go negative")]
        [TestCase(8, 2, 0, 8L, null, 6, TestName = "a counter with no count does not clamp")]
        [TestCase(8, -3, -1, null, null, 8, TestName = "negative inputs count as zero")]
        public void OpenSeatsClampToTheGameLimitAndThePlayersFreeCapacity(int max, int connected, int expected, long? capacity, long? count, int seats)
        {
            Assert.That(JoinablePlan.OpenSeats(max, connected, expected, capacity, count), Is.EqualTo(seats));
        }

        [Test]
        public void NothingLiveAndSeatsOpenPublishes()
        {
            Assert.That(JoinablePlan.Next(3, default, 30, T0), Is.EqualTo(JoinableStep.Publish));
        }

        [Test]
        public void ALiveRecordIsRepublishedWhenSeatsChangeAndAtHalfItsLifetime()
        {
            var live = new JoinableState { LiveSeats = 3, LastPublishAt = T0, MaybeLive = true };
            Assert.That(JoinablePlan.Next(3, live, 30, T0.AddSeconds(14.9)), Is.EqualTo(JoinableStep.None), "unchanged and not yet due");
            Assert.That(JoinablePlan.Next(2, live, 30, T0.AddSeconds(1)), Is.EqualTo(JoinableStep.Publish), "seats changed");
            Assert.That(JoinablePlan.Next(3, live, 30, T0.AddSeconds(15)), Is.EqualTo(JoinableStep.Publish), "ttl / 2 reached");
            Assert.That(JoinablePlan.Next(3, live, 10, T0.AddSeconds(5)), Is.EqualTo(JoinableStep.Publish), "the cadence follows the ttl");
            Assert.That(JoinablePlan.RepublishInterval(30), Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(JoinablePlan.NextDue(live, 30, T0.AddSeconds(5)), Is.EqualTo(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void ZeroSeatsWithdrawsALiveOrPossiblyLiveRecordAndOtherwiseDoesNothing()
        {
            Assert.That(JoinablePlan.Next(0, new JoinableState { LiveSeats = 2, LastPublishAt = T0, MaybeLive = true }, 30, T0), Is.EqualTo(JoinableStep.Withdraw));
            Assert.That(JoinablePlan.Next(0, new JoinableState { MaybeLive = true }, 30, T0), Is.EqualTo(JoinableStep.Withdraw), "a publish whose answer was lost may be stored");
            Assert.That(JoinablePlan.Next(0, default, 30, T0), Is.EqualTo(JoinableStep.None), "nothing to withdraw");
            Assert.That(JoinablePlan.NextDue(default, 30, T0), Is.Null, "only new seats can change anything");
        }

        [Test]
        public void AFailureWaitsTheRetryDelayBeforeTryingAgain()
        {
            var failed = new JoinableState { LastFailureAt = T0, MaybeLive = true };
            Assert.That(JoinablePlan.Next(4, failed, 30, T0.AddSeconds(1.9)), Is.EqualTo(JoinableStep.None));
            Assert.That(JoinablePlan.Next(4, failed, 30, T0.AddSeconds(2)), Is.EqualTo(JoinableStep.Publish));
            Assert.That(JoinablePlan.Next(0, failed, 30, T0.AddSeconds(2)), Is.EqualTo(JoinableStep.Withdraw));
            Assert.That(JoinablePlan.NextDue(failed, 30, T0.AddSeconds(0.5)), Is.EqualTo(TimeSpan.FromSeconds(1.5)));
        }
    }
}
