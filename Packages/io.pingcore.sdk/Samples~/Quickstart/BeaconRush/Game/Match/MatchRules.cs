using System;
using System.Collections.Generic;
using UnityEngine;

namespace BeaconRush.Match
{
    /// <summary>
    /// The rules of a Beacon Rush match, pure. The arena is the square <c>[-ArenaHalfSize, ArenaHalfSize]</c>
    /// on the XZ plane (y up, seen from above by an orthographic camera); positions here are
    /// <c>Vector2(x, z)</c>. The game server is the only authority: it moves players from their input,
    /// spawns beacons and counts pickups.
    /// </summary>
    public static class MatchRules
    {
        /// <summary>The first player to reach this score wins at once.</summary>
        public const int ScoreLimit = 10;

        /// <summary>The most beacons on the field at one time.</summary>
        public const int MaxBeacons = 3;

        /// <summary>A beacon spawns every this many seconds while fewer than <see cref="MaxBeacons"/> are up.</summary>
        public const float BeaconIntervalSeconds = 4f;

        /// <summary>A player within this distance of a beacon picks it up.</summary>
        public const float PickupRadius = 1f;

        /// <summary>Half the arena's side, in units.</summary>
        public const float ArenaHalfSize = 9f;

        /// <summary>A beacon never spawns closer than this to the arena's edge.</summary>
        public const float BeaconMargin = 1f;

        /// <summary>Player speed at full input, units per second.</summary>
        public const float PlayerSpeed = 6f;

        /// <summary>Input older than this is treated as no input, so a stalled client stops.</summary>
        public const float InputTimeoutSeconds = 0.5f;

        /// <summary>The input vector clamped to length 1; NaN or infinite components become 0.</summary>
        public static Vector2 ClampInput(Vector2 input)
        {
            if (float.IsNaN(input.x) || float.IsInfinity(input.x) || float.IsNaN(input.y) || float.IsInfinity(input.y))
            {
                return Vector2.zero;
            }

            return input.sqrMagnitude > 1f ? input.normalized : input;
        }

        /// <summary>One movement step: position plus clamped input times speed times <paramref name="seconds"/>, kept inside the arena.</summary>
        public static Vector2 Step(Vector2 position, Vector2 input, float seconds)
        {
            if (seconds <= 0f || float.IsNaN(seconds))
            {
                return ClampToArena(position);
            }

            return ClampToArena(position + ClampInput(input) * (PlayerSpeed * seconds));
        }

        /// <summary>The point kept inside the arena.</summary>
        public static Vector2 ClampToArena(Vector2 position)
        {
            return new Vector2(Mathf.Clamp(position.x, -ArenaHalfSize, ArenaHalfSize), Mathf.Clamp(position.y, -ArenaHalfSize, ArenaHalfSize));
        }

        /// <summary>Where a player spawns: one of eight points on a circle, by client id, so players do not start on top of each other.</summary>
        public static Vector2 SpawnPoint(ulong clientId)
        {
            double angle = (clientId % 8) * (Math.PI / 4);
            float radius = ArenaHalfSize * 0.6f;
            return new Vector2((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
        }

        /// <summary>A random beacon position inside the arena's margin.</summary>
        public static Vector2 BeaconPoint(System.Random random)
        {
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            float span = ArenaHalfSize - BeaconMargin;
            return new Vector2((float)(random.NextDouble() * 2 - 1) * span, (float)(random.NextDouble() * 2 - 1) * span);
        }

        /// <summary>The index of the first beacon within <see cref="PickupRadius"/> of <paramref name="player"/>, or -1.</summary>
        public static int PickupIndex(Vector2 player, IReadOnlyList<Vector2> beacons)
        {
            if (beacons == null)
            {
                return -1;
            }

            float limit = PickupRadius * PickupRadius;
            for (int i = 0; i < beacons.Count; i++)
            {
                if ((beacons[i] - player).sqrMagnitude <= limit)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// The beacon spawn clock, pure: one beacon every <see cref="MatchRules.BeaconIntervalSeconds"/> while fewer than
    /// <see cref="MatchRules.MaxBeacons"/> are up. The clock only runs while there is room, so a beacon picked up from a
    /// full field is replaced a whole interval later, never at once.
    /// </summary>
    public sealed class BeaconSpawnClock
    {
        private float sinceLast;

        /// <summary>How many beacons to spawn after <paramref name="seconds"/> with <paramref name="active"/> on the field (0 or 1).</summary>
        public int Tick(float seconds, int active)
        {
            if (active >= MatchRules.MaxBeacons)
            {
                sinceLast = 0f;
                return 0;
            }

            if (seconds > 0f && !float.IsNaN(seconds))
            {
                sinceLast += seconds;
            }

            if (sinceLast < MatchRules.BeaconIntervalSeconds)
            {
                return 0;
            }

            sinceLast -= MatchRules.BeaconIntervalSeconds;
            if (sinceLast >= MatchRules.BeaconIntervalSeconds)
            {
                // A long frame never bursts: at most one beacon per tick, and the clock does not bank time.
                sinceLast = 0f;
            }

            return 1;
        }

        /// <summary>Starts the clock over (a new match): the first beacon comes after one interval.</summary>
        public void Reset() => sinceLast = 0f;
    }

    /// <summary>One player's score, for <see cref="ScoreRules"/>.</summary>
    public readonly struct PlayerScore
    {
        public PlayerScore(ulong clientId, int score)
        {
            ClientId = clientId;
            Score = score;
        }

        public ulong ClientId { get; }

        public int Score { get; }
    }

    /// <summary>How a match ended: the winner, or none for a draw or a match with no players.</summary>
    public readonly struct MatchOutcome
    {
        public MatchOutcome(ulong? winner, int topScore)
        {
            Winner = winner;
            TopScore = topScore;
        }

        /// <summary>The winner's client id, or null for a draw (two or more share the top score) or no players.</summary>
        public ulong? Winner { get; }

        public int TopScore { get; }
    }

    /// <summary>Who won, pure: the first to <see cref="MatchRules.ScoreLimit"/>, or the single leader when the time ran out.</summary>
    public static class ScoreRules
    {
        /// <summary>True when <paramref name="score"/> ends the match.</summary>
        public static bool ReachedLimit(int score) => score >= MatchRules.ScoreLimit;

        /// <summary>The outcome over <paramref name="scores"/>: the single highest score wins; a shared top score is a draw.</summary>
        public static MatchOutcome Decide(IReadOnlyList<PlayerScore> scores)
        {
            if (scores == null || scores.Count == 0)
            {
                return new MatchOutcome(null, 0);
            }

            int top = int.MinValue;
            ulong? leader = null;
            bool shared = false;
            foreach (PlayerScore score in scores)
            {
                if (score.Score > top)
                {
                    top = score.Score;
                    leader = score.ClientId;
                    shared = false;
                }
                else if (score.Score == top)
                {
                    shared = true;
                }
            }

            return new MatchOutcome(shared ? null : leader, top);
        }
    }

    /// <summary>The bots' steering (<see cref="Session.SessionSettings.Bots"/>), pure: full speed towards the nearest beacon, or stand still with none up.</summary>
    public static class BotSteering
    {
        /// <summary>A unit vector from <paramref name="position"/> to the nearest beacon; zero with no beacon or when already on it.</summary>
        public static Vector2 Toward(Vector2 position, IReadOnlyList<Vector2> beacons)
        {
            if (beacons == null || beacons.Count == 0)
            {
                return Vector2.zero;
            }

            Vector2 nearest = beacons[0];
            float best = (nearest - position).sqrMagnitude;
            for (int i = 1; i < beacons.Count; i++)
            {
                float distance = (beacons[i] - position).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    nearest = beacons[i];
                }
            }

            Vector2 direction = nearest - position;
            return direction.sqrMagnitude < 1e-6f ? Vector2.zero : direction.normalized;
        }
    }
}
