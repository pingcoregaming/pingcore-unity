using BeaconRush.Hosting;
using BeaconRush.Networking;
using NUnit.Framework;
using PingCore.Fleet.Sessions;

namespace BeaconRush.Tests.Editor
{
    /// <summary>
    /// What the hosted game server feeds the joinable record: the connected players and the backfill players still on
    /// their way (<see cref="BackfillExpectations"/>), against the eight seats and the <c>players</c> counter's capacity.
    /// </summary>
    public sealed class JoinableInputsTests
    {
        [Test]
        public void TwoPlayersInAMatchOfEightOfferSixSeats()
        {
            var expected = new BackfillExpectations();
            Assert.That(JoinablePlan.OpenSeats(BeaconRushProtocol.MaxPlayers, 2, expected.Pending, 8, 2), Is.EqualTo(6));
        }

        [Test]
        public void ADeliveredBackfillHoldsItsSeatsUntilItsPlayersArrive()
        {
            var expected = new BackfillExpectations();
            Assert.That(expected.Add("bf-1", 1), Is.True);
            Assert.That(expected.Pending, Is.EqualTo(1));
            Assert.That(JoinablePlan.OpenSeats(BeaconRushProtocol.MaxPlayers, 2, expected.Pending, 8, 2), Is.EqualTo(5), "the seat is promised, not offered again");

            expected.Arrived("bf-1");
            Assert.That(expected.Pending, Is.EqualTo(0));
            Assert.That(JoinablePlan.OpenSeats(BeaconRushProtocol.MaxPlayers, 3, expected.Pending, 8, 3), Is.EqualTo(5), "now connected instead of expected");
        }

        [Test]
        public void ARepeatedBackfillIsCountedOnce()
        {
            var expected = new BackfillExpectations();
            expected.Add("bf-1", 2);
            Assert.That(expected.Add("bf-1", 2), Is.False);
            Assert.That(expected.Pending, Is.EqualTo(2));
        }

        [Test]
        public void ArrivalsNeverGoBelowZeroAndUnknownIdsChangeNothing()
        {
            var expected = new BackfillExpectations();
            expected.Add("bf-1", 1);
            expected.Arrived("bf-1");
            expected.Arrived("bf-1");
            expected.Arrived("bf-unknown");
            expected.Arrived(null);
            Assert.That(expected.Pending, Is.EqualTo(0));
            Assert.That(expected.Add(null, 3), Is.False);
            Assert.That(expected.Add(string.Empty, 3), Is.False);
            Assert.That(expected.Pending, Is.EqualTo(0));
        }

        [Test]
        public void SeveralBackfillsAddUpAndAClosedSessionForgetsThem()
        {
            var expected = new BackfillExpectations();
            expected.Add("bf-1", 2);
            expected.Add("bf-2", 3);
            expected.Arrived("bf-2");
            Assert.That(expected.Pending, Is.EqualTo(4));
            expected.Clear();
            Assert.That(expected.Pending, Is.EqualTo(0));
            Assert.That(expected.Add("bf-1", 1), Is.True, "a new session may see the id again");
        }

        [Test]
        public void TheCounterCapacityClampsTheSeatsAndTheOverrideIsAboveIt()
        {
            // The keeper clamps to the players counter's free capacity; a fixed override of 20 seats (SessionSettings.JoinableOpenSeats) is deliberately
            // above it, which is why it bypasses the keeper.
            Assert.That(JoinablePlan.OpenSeats(20, 2, 0, 8, 2), Is.EqualTo(6));
            Assert.That(JoinablePlan.OpenSeats(BeaconRushProtocol.MaxPlayers, 8, 0, 8, 8), Is.EqualTo(0), "full: withdraw");
        }
    }
}
