using System;
using System.Collections.Generic;
using NUnit.Framework;
using PingCore.Core.Discovery;

namespace PingCore.Discovery.Host.Tests.Editor
{
    public sealed class HeartbeatScheduleTests
    {
        private static DiscoveryCallResult Result(DiscoveryOutcome outcome, int status, TimeSpan? retryAfter = null)
        {
            return new DiscoveryCallResult(outcome, status, null, null, null, retryAfter, null);
        }

        [TestCase(0.0, 27.0)]
        [TestCase(0.5, 30.0)]
        [TestCase(0.25, 28.5)]
        [TestCase(0.75, 31.5)]
        public void TheRegularWaitIsThirtySecondsPlusAJitterOfUpToThreeEitherWay(double unit, double seconds)
        {
            Assert.That(HeartbeatSchedule.Regular(unit).TotalSeconds, Is.EqualTo(seconds).Within(1e-6));
        }

        [Test]
        public void TheRegularWaitNeverLeavesTwentySevenToThirtyThreeSecondsWhateverTheJitterSourceReturns()
        {
            foreach (double unit in new[] { -5.0, -0.0001, 0.0, 0.1, 0.999999, 1.0, 7.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                TimeSpan wait = HeartbeatSchedule.Regular(unit);
                Assert.That(wait.TotalSeconds, Is.GreaterThanOrEqualTo(27.0).And.LessThan(33.0), "unit " + unit);
            }

            // The bound check can fail: three beats per 90 s TTL need the wait below 33 s.
            Assert.That(HeartbeatSchedule.Regular(0.999999).TotalSeconds, Is.GreaterThan(32.99));
            Assert.That(HeartbeatSchedule.Interval + HeartbeatSchedule.Jitter, Is.LessThanOrEqualTo(TimeSpan.FromTicks(HeartbeatSchedule.Ttl.Ticks / 2)));
        }

        [Test]
        public void TransientFailuresRetryAtFiveTenThenEveryTwentySecondsBelowTheTtl()
        {
            Assert.That(HeartbeatSchedule.Retry(1), Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(HeartbeatSchedule.Retry(2), Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(HeartbeatSchedule.Retry(3), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(HeartbeatSchedule.Retry(50), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(HeartbeatSchedule.RetryCap, Is.LessThan(HeartbeatSchedule.Ttl));
        }

        [Test]
        public void EachOutcomeLeadsToItsStepAndFailureCount()
        {
            var rows = new List<(DiscoveryCallResult result, int before, HeartbeatStepKind kind, double seconds, int failures, string why)>
            {
                (Result(DiscoveryOutcome.Ok, 200), 3, HeartbeatStepKind.Continue, 30, 0, "accepted: regular cadence, failures reset"),
                (Result(DiscoveryOutcome.Conflict, 409), 0, HeartbeatStepKind.Stop, 0, 1, "409: stop"),
                (Result(DiscoveryOutcome.Cancelled, 0), 2, HeartbeatStepKind.Cancelled, 0, 2, "stopping"),
                (Result(DiscoveryOutcome.RateLimited, 429, TimeSpan.FromSeconds(42)), 0, HeartbeatStepKind.Continue, 42, 1, "429 waits Retry-After"),
                (Result(DiscoveryOutcome.RateLimited, 429, TimeSpan.Zero), 0, HeartbeatStepKind.Continue, 1, 1, "429 with Retry-After 0 waits the 1 s floor"),
                (Result(DiscoveryOutcome.RateLimited, 429), 0, HeartbeatStepKind.Continue, 30, 1, "429 without Retry-After keeps the cadence"),
                (Result(DiscoveryOutcome.Degraded, 503), 0, HeartbeatStepKind.Continue, 5, 1, "503 first retry"),
                (Result(DiscoveryOutcome.Degraded, 503), 1, HeartbeatStepKind.Continue, 10, 2, "503 second retry"),
                (Result(DiscoveryOutcome.Unreachable, 0), 2, HeartbeatStepKind.Continue, 20, 3, "no answer, third retry"),
                (Result(DiscoveryOutcome.Unexpected, 500), 5, HeartbeatStepKind.Continue, 20, 6, "500 capped"),
                (Result(DiscoveryOutcome.Unexpected, 0), 0, HeartbeatStepKind.Continue, 5, 1, "unparseable success body"),
                (Result(DiscoveryOutcome.Unauthorized, 401), 0, HeartbeatStepKind.Continue, 30, 1, "401 keeps the cadence"),
                (Result(DiscoveryOutcome.InvalidRequest, 400), 1, HeartbeatStepKind.Continue, 30, 2, "400 keeps the cadence"),
                (Result(DiscoveryOutcome.Forbidden, 403), 0, HeartbeatStepKind.Continue, 30, 1, "403 keeps the cadence"),
                (Result(DiscoveryOutcome.Unexpected, 418), 0, HeartbeatStepKind.Continue, 30, 1, "an unexpected 4xx keeps the cadence"),
            };
            foreach (var row in rows)
            {
                HeartbeatStep step = HeartbeatSchedule.Next(row.result, row.before, 0.5);
                Assert.That(step.Kind, Is.EqualTo(row.kind), row.why);
                Assert.That(step.Failures, Is.EqualTo(row.failures), row.why);
                if (row.kind == HeartbeatStepKind.Continue)
                {
                    Assert.That(step.Delay.TotalSeconds, Is.EqualTo(row.seconds).Within(1e-6), row.why);
                }
            }
        }

        [Test]
        public void AChangeIsSentEarlyAtMostOncePerFiveSecondsAndNeverDuringABackoff()
        {
            var last = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset regular = last + TimeSpan.FromSeconds(30);

            Assert.That(HeartbeatSchedule.DueAt(regular, false, 0, last), Is.EqualTo(regular), "no change: regular");
            Assert.That(HeartbeatSchedule.DueAt(regular, true, 0, last), Is.EqualTo(last + TimeSpan.FromSeconds(5)), "a change: 5 s after the last send");
            Assert.That(HeartbeatSchedule.DueAt(regular, true, 1, last), Is.EqualTo(regular), "a change during a backoff waits for it");
            DateTimeOffset soon = last + TimeSpan.FromSeconds(3);
            Assert.That(HeartbeatSchedule.DueAt(soon, true, 0, last), Is.EqualTo(soon), "an earlier regular beat wins");
        }
    }
}
