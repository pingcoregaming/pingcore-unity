using System;
using System.Collections.Generic;
using BeaconRush.Networking;
using BeaconRush.Session;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BeaconRush.Match
{
    /// <summary>
    /// The game server side of a match, driven by <c>GameServerRuntime</c> every frame: it spawns the
    /// <see cref="ScoreBoard"/> once, keeps the board's rows in step with the session's players, and while a match
    /// runs moves every player (by its input, or a bot's), spawns beacons on the
    /// <see cref="BeaconSpawnClock"/> and scores pickups. The rules are <see cref="MatchRules"/> and
    /// <see cref="ScoreRules"/>; this class only applies them to network objects. Main thread only.
    /// </summary>
    internal sealed class MatchController
    {
        private readonly NetworkManager network;
        private readonly GameObject beaconPrefab;
        private readonly GameObject scoreBoardPrefab;
        private readonly List<Beacon> beacons = new List<Beacon>();
        private readonly List<Vector2> beaconPoints = new List<Vector2>();
        private readonly BeaconSpawnClock spawnClock = new BeaconSpawnClock();
        private System.Random random = new System.Random();
        private ScoreBoard board;
        private int pickups;

        public MatchController(NetworkManager network, GameObject beaconPrefab, GameObject scoreBoardPrefab)
        {
            this.network = network != null ? network : throw new ArgumentNullException(nameof(network));
            this.beaconPrefab = beaconPrefab != null ? beaconPrefab : throw new ArgumentNullException(nameof(beaconPrefab));
            this.scoreBoardPrefab = scoreBoardPrefab != null ? scoreBoardPrefab : throw new ArgumentNullException(nameof(scoreBoardPrefab));
        }

        /// <summary>Beacons picked up in the current match.</summary>
        public int Pickups => pickups;

        /// <summary>Spawns the score board; call once, right after the game server started listening.</summary>
        public void SpawnBoard()
        {
            if (board != null)
            {
                return;
            }

            GameObject instance = Object.Instantiate(scoreBoardPrefab);
            instance.name = scoreBoardPrefab.name;
            board = instance.GetComponent<ScoreBoard>();
            instance.GetComponent<NetworkObject>().Spawn(true);
        }

        public void AddPlayer(ulong clientId, string name)
        {
            if (board != null)
            {
                board.ServerAddPlayer(clientId, name);
            }
        }

        public void RemovePlayer(ulong clientId)
        {
            if (board != null)
            {
                board.ServerRemovePlayer(clientId);
            }
        }

        /// <summary>A phase began: a match starts from zero, results and lobby clear the field.</summary>
        public void OnPhase(bool sessionOpen, SessionPhase phase, TimeSpan timeLeft)
        {
            if (board != null)
            {
                board.ServerSetPhase(sessionOpen, phase, SecondsLeft(timeLeft));
            }

            switch (phase)
            {
                case SessionPhase.Match:
                    ClearBeacons();
                    pickups = 0;
                    spawnClock.Reset();
                    random = new System.Random();
                    if (board != null)
                    {
                        board.ServerResetScores();
                        board.ServerSetWinner(null);
                    }

                    foreach (BeaconRushPlayer player in Players())
                    {
                        player.ServerRespawn();
                    }

                    break;
                default:
                    ClearBeacons();
                    break;
            }
        }

        /// <summary>The session closed: empty the board and the field.</summary>
        public void OnSessionClosed()
        {
            ClearBeacons();
            if (board != null)
            {
                board.ServerClear();
                board.ServerSetPhase(false, SessionPhase.Lobby, 0);
            }
        }

        /// <summary>The match result over the board's rows, and it is shown on the board.</summary>
        public MatchOutcome Finish()
        {
            MatchOutcome outcome = board == null ? new MatchOutcome(null, 0) : ScoreRules.Decide(board.ServerScores());
            if (board != null)
            {
                board.ServerSetWinner(outcome.Winner);
            }

            return outcome;
        }

        /// <summary>One frame. Returns true when a player reached the score limit during it (the match ends).</summary>
        public bool Tick(float seconds, SessionPhase phase, TimeSpan timeLeft, bool bots)
        {
            if (board != null)
            {
                board.ServerSetSecondsLeft(SecondsLeft(timeLeft));
            }

            if (phase != SessionPhase.Match || seconds <= 0f)
            {
                return false;
            }

            PruneBeacons();
            if (spawnClock.Tick(seconds, beacons.Count) > 0)
            {
                SpawnBeacon();
            }

            bool limit = false;
            foreach (BeaconRushPlayer player in Players())
            {
                Vector2 input = bots ? BotSteering.Toward(player.Point, beaconPoints) : player.ServerInput;
                player.ServerStep(input, seconds);
                int hit = MatchRules.PickupIndex(player.Point, beaconPoints);
                if (hit < 0)
                {
                    continue;
                }

                RemoveBeaconAt(hit);
                pickups++;
                int score = board == null ? -1 : board.ServerAddPoint(player.OwnerClientId);
                if (ScoreRules.ReachedLimit(score))
                {
                    limit = true;
                    break;
                }
            }

            return limit;
        }

        private static int SecondsLeft(TimeSpan left)
        {
            double seconds = Math.Ceiling(left.TotalSeconds);
            return seconds > 86400 ? 0 : (int)Math.Max(0, seconds);
        }

        private IEnumerable<BeaconRushPlayer> Players()
        {
            var list = new List<BeaconRushPlayer>();
            if (network == null || !network.IsListening)
            {
                return list;
            }

            foreach (NetworkClient client in network.ConnectedClientsList)
            {
                BeaconRushPlayer player = client.PlayerObject == null ? null : client.PlayerObject.GetComponent<BeaconRushPlayer>();
                if (player != null)
                {
                    list.Add(player);
                }
            }

            return list;
        }

        private void SpawnBeacon()
        {
            Vector2 point = MatchRules.BeaconPoint(random);
            GameObject instance = Object.Instantiate(beaconPrefab, new Vector3(point.x, 0f, point.y), Quaternion.identity);
            instance.name = beaconPrefab.name;
            instance.GetComponent<NetworkObject>().Spawn(true);
            beacons.Add(instance.GetComponent<Beacon>());
            beaconPoints.Add(point);
        }

        private void RemoveBeaconAt(int index)
        {
            Beacon beacon = beacons[index];
            beacons.RemoveAt(index);
            beaconPoints.RemoveAt(index);
            if (beacon != null && beacon.IsSpawned)
            {
                beacon.NetworkObject.Despawn(true);
            }
        }

        private void PruneBeacons()
        {
            for (int i = beacons.Count - 1; i >= 0; i--)
            {
                if (beacons[i] == null)
                {
                    beacons.RemoveAt(i);
                    beaconPoints.RemoveAt(i);
                }
            }
        }

        private void ClearBeacons()
        {
            for (int i = beacons.Count - 1; i >= 0; i--)
            {
                RemoveBeaconAt(i);
            }
        }
    }
}
