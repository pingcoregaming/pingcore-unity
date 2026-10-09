using System;
using System.Collections.Generic;
using BeaconRush.Session;
using Unity.Collections;
using Unity.Netcode;

namespace BeaconRush.Match
{
    /// <summary>One row of the <see cref="ScoreBoard"/>: an NGO client id, its display name and its score.</summary>
    public struct ScoreEntry : INetworkSerializable, IEquatable<ScoreEntry>
    {
        public ulong ClientId;
        public FixedString32Bytes Name;
        public int Score;

        public ScoreEntry(ulong clientId, FixedString32Bytes name, int score)
        {
            ClientId = clientId;
            Name = name;
            Score = score;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ClientId);
            serializer.SerializeValue(ref Name);
            serializer.SerializeValue(ref Score);
        }

        public bool Equals(ScoreEntry other) => ClientId == other.ClientId && Name.Equals(other.Name) && Score == other.Score;

        public override bool Equals(object obj) => obj is ScoreEntry other && Equals(other);

        public override int GetHashCode() => ClientId.GetHashCode() ^ Score;
    }

    /// <summary>
    /// The match state every client sees (<c>Prefabs/ScoreBoard.prefab</c>), spawned once by the game server when it
    /// starts listening and kept for the process: the scores (<see cref="Entries"/>), the phase, the seconds left in
    /// it and the last match's winner. Only the game server writes it. A client reads <see cref="Instance"/> and
    /// listens to <see cref="Changed"/>.
    /// </summary>
    public sealed class ScoreBoard : NetworkBehaviour
    {
        /// <summary>No winner (a draw, or no match yet).</summary>
        public const ulong NoWinner = ulong.MaxValue;

        private NetworkList<ScoreEntry> entries;
        private readonly NetworkVariable<byte> phase = new NetworkVariable<byte>((byte)SessionPhase.Lobby);
        private readonly NetworkVariable<int> secondsLeft = new NetworkVariable<int>();
        private readonly NetworkVariable<ulong> winner = new NetworkVariable<ulong>(NoWinner);
        private readonly NetworkVariable<bool> inSession = new NetworkVariable<bool>();

        /// <summary>The spawned score board of this process (game server, listen host or client), or null.</summary>
        public static ScoreBoard Instance { get; private set; }

        /// <summary>Raised on every change, on whichever side it is read.</summary>
        public event Action Changed;

        /// <summary>The scores, highest first.</summary>
        public IReadOnlyList<ScoreEntry> Entries
        {
            get
            {
                var list = new List<ScoreEntry>();
                if (entries != null)
                {
                    foreach (ScoreEntry entry in entries)
                    {
                        list.Add(entry);
                    }
                }

                list.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.ClientId.CompareTo(b.ClientId));
                return list;
            }
        }

        public SessionPhase Phase => (SessionPhase)phase.Value;

        /// <summary>True while a session is open (an idle hosted game server shows false).</summary>
        public bool InSession => inSession.Value;

        /// <summary>Whole seconds left in the current phase (an unbounded lobby shows 0).</summary>
        public int SecondsLeft => secondsLeft.Value;

        /// <summary>The last match's winner, or null for a draw or before the first result.</summary>
        public ulong? Winner => winner.Value == NoWinner ? (ulong?)null : winner.Value;

        private void Awake()
        {
            entries = new NetworkList<ScoreEntry>();
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            entries.OnListChanged += OnListChanged;
            phase.OnValueChanged += OnByteChanged;
            secondsLeft.OnValueChanged += OnIntChanged;
            winner.OnValueChanged += OnUlongChanged;
            inSession.OnValueChanged += OnBoolChanged;
            RaiseChanged();
        }

        public override void OnNetworkDespawn()
        {
            entries.OnListChanged -= OnListChanged;
            phase.OnValueChanged -= OnByteChanged;
            secondsLeft.OnValueChanged -= OnIntChanged;
            winner.OnValueChanged -= OnUlongChanged;
            inSession.OnValueChanged -= OnBoolChanged;
            if (Instance == this)
            {
                Instance = null;
            }
        }

        public override void OnDestroy()
        {
            entries?.Dispose();
            base.OnDestroy();
        }

        // ---- game server only ---------------------------------------------------------------------

        internal void ServerSetPhase(bool open, SessionPhase value, int seconds)
        {
            inSession.Value = open;
            phase.Value = (byte)value;
            secondsLeft.Value = Math.Max(0, seconds);
        }

        internal void ServerSetSecondsLeft(int seconds)
        {
            int value = Math.Max(0, seconds);
            if (secondsLeft.Value != value)
            {
                secondsLeft.Value = value;
            }
        }

        internal void ServerSetWinner(ulong? clientId) => winner.Value = clientId ?? NoWinner;

        internal void ServerAddPlayer(ulong clientId, string name)
        {
            if (IndexOf(clientId) < 0)
            {
                entries.Add(new ScoreEntry(clientId, DisplayNames.ToFixed(name, clientId), 0));
            }
        }

        internal void ServerRemovePlayer(ulong clientId)
        {
            int index = IndexOf(clientId);
            if (index >= 0)
            {
                entries.RemoveAt(index);
            }
        }

        /// <summary>One point for <paramref name="clientId"/>; returns the new score, or -1 for a client not on the board.</summary>
        internal int ServerAddPoint(ulong clientId)
        {
            int index = IndexOf(clientId);
            if (index < 0)
            {
                return -1;
            }

            ScoreEntry entry = entries[index];
            entry.Score++;
            entries[index] = entry;
            return entry.Score;
        }

        internal void ServerResetScores()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ScoreEntry entry = entries[i];
                if (entry.Score != 0)
                {
                    entry.Score = 0;
                    entries[i] = entry;
                }
            }
        }

        internal void ServerClear()
        {
            entries.Clear();
            winner.Value = NoWinner;
        }

        internal List<PlayerScore> ServerScores()
        {
            var list = new List<PlayerScore>();
            foreach (ScoreEntry entry in entries)
            {
                list.Add(new PlayerScore(entry.ClientId, entry.Score));
            }

            return list;
        }

        private int IndexOf(ulong clientId)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].ClientId == clientId)
                {
                    return i;
                }
            }

            return -1;
        }

        private void OnListChanged(NetworkListEvent<ScoreEntry> change) => RaiseChanged();

        private void OnByteChanged(byte previous, byte current) => RaiseChanged();

        private void OnIntChanged(int previous, int current) => RaiseChanged();

        private void OnUlongChanged(ulong previous, ulong current) => RaiseChanged();

        private void OnBoolChanged(bool previous, bool current) => RaiseChanged();

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }
    }
}
