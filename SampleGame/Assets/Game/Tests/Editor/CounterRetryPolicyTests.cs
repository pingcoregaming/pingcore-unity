using BeaconRush.Hosting;
using NUnit.Framework;
using PingCore.Fleet;

namespace BeaconRush.Tests.Editor
{
    public sealed class CounterRetryPolicyTests
    {
        [TestCase(FleetCallOutcome.Ok, 0, CounterWriteStep.Written)]
        [TestCase(FleetCallOutcome.Ok, 3, CounterWriteStep.Written, Description = "a success on the last retry still counts")]
        [TestCase(FleetCallOutcome.Unreachable, 0, CounterWriteStep.Retry)]
        [TestCase(FleetCallOutcome.Unreachable, 2, CounterWriteStep.Retry)]
        [TestCase(FleetCallOutcome.Unreachable, 3, CounterWriteStep.GiveUp, Description = "three retries, then report once")]
        [TestCase(FleetCallOutcome.Rejected, 0, CounterWriteStep.Retry)]
        [TestCase(FleetCallOutcome.Rejected, 3, CounterWriteStep.GiveUp)]
        [TestCase(FleetCallOutcome.Unsupported, 0, CounterWriteStep.Retry)]
        [TestCase(FleetCallOutcome.EndpointClosed, 0, CounterWriteStep.Stop, Description = "the endpoint closed: the process is stopping, no retry")]
        [TestCase(FleetCallOutcome.Cancelled, 0, CounterWriteStep.Stop)]
        [TestCase(FleetCallOutcome.Inert, 0, CounterWriteStep.Stop)]
        public void TheStepAfterAWriteFollowsItsOutcomeAndTheRetriesSoFar(FleetCallOutcome outcome, int retriesSoFar, CounterWriteStep expected)
        {
            Assert.That(CounterRetryPolicy.Next(outcome, retriesSoFar), Is.EqualTo(expected));
        }

        [Test]
        public void AFailingWriteIsTriedFourTimesInAllThenGivenUp()
        {
            int attempts = 0;
            int retries = 0;
            CounterWriteStep step;
            do
            {
                attempts++;
                step = CounterRetryPolicy.Next(FleetCallOutcome.Unreachable, retries);
                if (step == CounterWriteStep.Retry)
                {
                    retries++;
                }
            }
            while (step == CounterWriteStep.Retry && attempts < 100);

            Assert.That(step, Is.EqualTo(CounterWriteStep.GiveUp));
            Assert.That(attempts, Is.EqualTo(CounterRetryPolicy.MaxRetries + 1));
            Assert.That(CounterRetryPolicy.MaxRetries, Is.EqualTo(3));
            Assert.That(CounterRetryPolicy.RetryDelay.TotalSeconds, Is.EqualTo(1));
        }

        [Test]
        public void ACountThatChangesMidRetryStartsItsOwnRetryBudget()
        {
            var retry = new CounterRetryState();

            // players = 1 fails on the first try and all three retries...
            for (int i = 0; i < CounterRetryPolicy.MaxRetries; i++)
            {
                Assert.That(retry.After(1, FleetCallOutcome.Unreachable), Is.EqualTo(CounterWriteStep.Retry), "try " + (i + 1) + " of players = 1");
            }

            Assert.That(retry.Retries, Is.EqualTo(CounterRetryPolicy.MaxRetries));

            // ...then a player joins before the last try: players = 2 is a new count, not the fifth try of the old one.
            Assert.That(retry.After(2, FleetCallOutcome.Unreachable), Is.EqualTo(CounterWriteStep.Retry), "the first try of players = 2 is retried");
            Assert.That(retry.Retries, Is.EqualTo(1));
            Assert.That(retry.After(2, FleetCallOutcome.Unreachable), Is.EqualTo(CounterWriteStep.Retry));
            Assert.That(retry.After(2, FleetCallOutcome.Unreachable), Is.EqualTo(CounterWriteStep.Retry));
            Assert.That(retry.After(2, FleetCallOutcome.Unreachable), Is.EqualTo(CounterWriteStep.GiveUp), "players = 2 gets four tries in all");
        }

        [Test]
        public void TheSameCountKeepsItsRetriesAndAWriteClearsThem()
        {
            var retry = new CounterRetryState();
            Assert.That(retry.After(3, FleetCallOutcome.Rejected), Is.EqualTo(CounterWriteStep.Retry));
            Assert.That(retry.After(3, FleetCallOutcome.Rejected), Is.EqualTo(CounterWriteStep.Retry));
            Assert.That(retry.Retries, Is.EqualTo(2), "the same count carries its retries");
            Assert.That(retry.After(3, FleetCallOutcome.Ok), Is.EqualTo(CounterWriteStep.Written));
            Assert.That(retry.Retries, Is.EqualTo(0));
            Assert.That(retry.After(4, FleetCallOutcome.EndpointClosed), Is.EqualTo(CounterWriteStep.Stop));
            Assert.That(retry.Retries, Is.EqualTo(0), "a stop is not a retry");
        }
    }
}
