using System.Collections.Generic;
using BeaconRush.Match;
using NUnit.Framework;
using UnityEngine;

namespace BeaconRush.Tests.Editor
{
    /// <summary>The pure rules of a match: scoring, the beacon clock, pickups, movement and the bots' steering.</summary>
    public sealed class MatchRulesTests
    {
        [TestCase(9, false)]
        [TestCase(10, true)]
        [TestCase(11, true)]
        [TestCase(0, false)]
        public void TheScoreLimitIsTen(int score, bool reached)
        {
            Assert.That(ScoreRules.ReachedLimit(score), Is.EqualTo(reached));
        }

        [Test]
        public void TheSingleLeaderWins()
        {
            MatchOutcome outcome = ScoreRules.Decide(new List<PlayerScore> { new PlayerScore(1, 3), new PlayerScore(2, 7), new PlayerScore(3, 5) });
            Assert.That(outcome.Winner, Is.EqualTo(2UL));
            Assert.That(outcome.TopScore, Is.EqualTo(7));
        }

        [Test]
        public void ASharedTopScoreIsADraw()
        {
            MatchOutcome outcome = ScoreRules.Decide(new List<PlayerScore> { new PlayerScore(1, 7), new PlayerScore(2, 7), new PlayerScore(3, 5) });
            Assert.That(outcome.Winner, Is.Null);
            Assert.That(outcome.TopScore, Is.EqualTo(7));
        }

        [Test]
        public void ADrawBelowALaterLeaderIsNoDraw()
        {
            // Two players tie at 2 first; a third with 4 must still win alone.
            MatchOutcome outcome = ScoreRules.Decide(new List<PlayerScore> { new PlayerScore(1, 2), new PlayerScore(2, 2), new PlayerScore(3, 4) });
            Assert.That(outcome.Winner, Is.EqualTo(3UL));
        }

        [Test]
        public void NoPlayersMeansNoWinner()
        {
            Assert.That(ScoreRules.Decide(new List<PlayerScore>()).Winner, Is.Null);
            Assert.That(ScoreRules.Decide(null).Winner, Is.Null);
            Assert.That(ScoreRules.Decide(new List<PlayerScore> { new PlayerScore(4, 0) }).Winner, Is.EqualTo(4UL), "a lone player wins even on zero");
        }

        [Test]
        public void ABeaconComesEveryFourSecondsWhileThereIsRoom()
        {
            var clock = new BeaconSpawnClock();
            Assert.That(clock.Tick(3.9f, 0), Is.EqualTo(0));
            Assert.That(clock.Tick(0.1f, 0), Is.EqualTo(1));
            Assert.That(clock.Tick(2f, 1), Is.EqualTo(0));
            Assert.That(clock.Tick(2f, 1), Is.EqualTo(1));
        }

        [Test]
        public void AFullFieldStopsTheClockSoAPickupIsReplacedAWholeIntervalLater()
        {
            var clock = new BeaconSpawnClock();
            Assert.That(clock.Tick(10f, MatchRules.MaxBeacons), Is.EqualTo(0), "three up: none");
            Assert.That(clock.Tick(0.5f, MatchRules.MaxBeacons - 1), Is.EqualTo(0), "the full time did not bank");
            Assert.That(clock.Tick(3.5f, MatchRules.MaxBeacons - 1), Is.EqualTo(1));
        }

        [Test]
        public void ALongFrameSpawnsOneBeaconAndBanksNothing()
        {
            var clock = new BeaconSpawnClock();
            Assert.That(clock.Tick(20f, 0), Is.EqualTo(1));
            Assert.That(clock.Tick(0.01f, 1), Is.EqualTo(0), "no burst after a long frame");
        }

        [Test]
        public void ResetStartsTheIntervalOver()
        {
            var clock = new BeaconSpawnClock();
            clock.Tick(3.9f, 0);
            clock.Reset();
            Assert.That(clock.Tick(0.2f, 0), Is.EqualTo(0));
        }

        [Test]
        public void APickupNeedsToBeWithinOneUnit()
        {
            var beacons = new List<Vector2> { new Vector2(5f, 5f), new Vector2(1f, 0f), new Vector2(0.5f, 0f) };
            Assert.That(MatchRules.PickupIndex(Vector2.zero, beacons), Is.EqualTo(1), "exactly 1 unit away is a pickup, and the first in order wins");
            Assert.That(MatchRules.PickupIndex(new Vector2(-1.01f, 0f), new List<Vector2> { Vector2.zero }), Is.EqualTo(-1));
            Assert.That(MatchRules.PickupIndex(Vector2.zero, new List<Vector2>()), Is.EqualTo(-1));
            Assert.That(MatchRules.PickupIndex(Vector2.zero, null), Is.EqualTo(-1));
        }

        [Test]
        public void MovementIsClampedToFullSpeedAndTheArena()
        {
            Vector2 step = MatchRules.Step(Vector2.zero, new Vector2(10f, 0f), 0.5f);
            Assert.That(step.x, Is.EqualTo(MatchRules.PlayerSpeed * 0.5f).Within(1e-4f), "an over-long input moves at full speed only");
            Assert.That(MatchRules.Step(new Vector2(8.9f, 0f), Vector2.right, 1f).x, Is.EqualTo(MatchRules.ArenaHalfSize), "the edge holds");
            Assert.That(MatchRules.Step(Vector2.zero, new Vector2(float.NaN, 1f), 1f), Is.EqualTo(Vector2.zero), "a NaN input does not move");
            Assert.That(MatchRules.Step(Vector2.one, Vector2.right, -1f), Is.EqualTo(Vector2.one), "no time, no move");
        }

        [Test]
        public void SpawnPointsAndBeaconsAreInsideTheArena()
        {
            var random = new System.Random(7);
            for (ulong id = 0; id < 16; id++)
            {
                Vector2 spawn = MatchRules.SpawnPoint(id);
                Assert.That(Mathf.Abs(spawn.x) <= MatchRules.ArenaHalfSize && Mathf.Abs(spawn.y) <= MatchRules.ArenaHalfSize, spawn.ToString());
                Vector2 beacon = MatchRules.BeaconPoint(random);
                float limit = MatchRules.ArenaHalfSize - MatchRules.BeaconMargin;
                Assert.That(Mathf.Abs(beacon.x) <= limit && Mathf.Abs(beacon.y) <= limit, beacon.ToString());
            }

            Assert.That(MatchRules.SpawnPoint(1), Is.Not.EqualTo(MatchRules.SpawnPoint(2)), "neighbouring players do not share a spawn");
        }

        [Test]
        public void ABotSteersAtFullSpeedToTheNearestBeacon()
        {
            var beacons = new List<Vector2> { new Vector2(-6f, 0f), new Vector2(0f, 3f), new Vector2(5f, 5f) };
            Vector2 steer = BotSteering.Toward(Vector2.zero, beacons);
            Assert.That(steer.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(steer.y, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void ABotWithNoBeaconOrOnTopOfOneStandsStill()
        {
            Assert.That(BotSteering.Toward(Vector2.one, new List<Vector2>()), Is.EqualTo(Vector2.zero));
            Assert.That(BotSteering.Toward(Vector2.one, null), Is.EqualTo(Vector2.zero));
            Assert.That(BotSteering.Toward(Vector2.one, new List<Vector2> { Vector2.one }), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void ABotReachesABeaconWithinAMatchFrame()
        {
            // The bots exist so a headless match can finish: steering and stepping must actually arrive.
            var beacons = new List<Vector2> { new Vector2(7f, -7f) };
            Vector2 position = MatchRules.SpawnPoint(4);
            for (int frame = 0; frame < 30 * 10 && MatchRules.PickupIndex(position, beacons) < 0; frame++)
            {
                position = MatchRules.Step(position, BotSteering.Toward(position, beacons), 1f / 30f);
            }

            Assert.That(MatchRules.PickupIndex(position, beacons), Is.EqualTo(0));
        }
    }
}
