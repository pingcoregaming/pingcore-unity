using BeaconRush.Client.Models;
using NUnit.Framework;
using PingCore.Core.Handshake;

namespace BeaconRush.Client.Tests
{
    /// <summary>Quick play's decisions: connect, fall back to Find match on no seats, retry once when the game server was just taken.</summary>
    public sealed class QuickPlayPlanTests
    {
        [TestCase(true, null, QuickPlayStep.Connect)]
        [TestCase(false, "no_seats", QuickPlayStep.FindMatchInstead)]
        [TestCase(false, "rate_limited", QuickPlayStep.Fail)]
        [TestCase(false, null, QuickPlayStep.Fail)]
        [TestCase(true, "no_seats", QuickPlayStep.Connect)]
        public void TheQuickJoinsAnswerDecidesTheNextStep(bool usable, string reason, QuickPlayStep expected)
        {
            Assert.That(QuickPlayPlan.AfterQuickJoin(usable, reason), Is.EqualTo(expected));
        }

        [TestCase("refused_by_game", 1, QuickPlayStep.RetryQuickPlay)]
        [TestCase("not_in_session", 1, QuickPlayStep.RetryQuickPlay)]
        [TestCase("server_full", 1, QuickPlayStep.RetryQuickPlay)]
        [TestCase("refused_by_game", 2, QuickPlayStep.Fail)]
        [TestCase("reservation_invalid", 1, QuickPlayStep.Fail)]
        [TestCase("protocol_mismatch", 1, QuickPlayStep.Fail)]
        [TestCase("connect_timeout", 1, QuickPlayStep.Fail)]
        [TestCase(null, 1, QuickPlayStep.Fail)]
        public void OnlyATakenGameServerIsRetriedAndOnlyOnce(string literal, int attempt, QuickPlayStep expected)
        {
            Assert.That(QuickPlayPlan.AfterRefusal(literal, attempt), Is.EqualTo(expected));
        }

        [Test]
        public void EveryRetriedLiteralIsARealRejectLiteral()
        {
            int retried = 0;
            foreach (string literal in JoinRejectReasons.AllWireValues())
            {
                if (QuickPlayPlan.AfterRefusal(literal, 1) == QuickPlayStep.RetryQuickPlay)
                {
                    retried++;
                }
            }

            Assert.That(retried, Is.EqualTo(3));
            Assert.That(QuickPlayPlan.MaxAttempts, Is.EqualTo(2));
        }
    }
}
