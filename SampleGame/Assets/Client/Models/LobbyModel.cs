using System;
using System.Collections.Generic;

namespace BeaconRush.Client.Models
{
    /// <summary>The session phase the client shows.</summary>
    public enum SessionView
    {
        /// <summary>Connected, waiting for the roster or the lobby timer.</summary>
        Lobby,

        /// <summary>The match is running.</summary>
        Match,

        /// <summary>The match is over.</summary>
        Results,
    }

    /// <summary>One player on the lobby roster or the score list.</summary>
    public sealed class LobbyEntry
    {
        /// <summary>Creates an entry.</summary>
        public LobbyEntry(ulong clientId, string name, int score, bool isLocal)
        {
            ClientId = clientId;
            Name = string.IsNullOrEmpty(name) ? "Player " + clientId : name;
            Score = score;
            IsLocal = isLocal;
        }

        /// <summary>The NGO client id.</summary>
        public ulong ClientId { get; }

        /// <summary>The display name.</summary>
        public string Name { get; }

        /// <summary>The score (0 before the match).</summary>
        public int Score { get; }

        /// <summary>True for this client's own player.</summary>
        public bool IsLocal { get; }
    }

    /// <summary>
    /// The Lobby, Match and Results screens, pure: the roster, the expected player count, the lobby timer
    /// and the phase. The client reports what it sees (players spawned, the phase the game server
    /// replicates, the scores); a game server that replicates no phase leaves the client in the lobby
    /// until the roster is complete, then shows the match.
    /// </summary>
    public sealed class LobbyModel
    {
        private DateTimeOffset? joinedAt;
        private SessionView? reportedPhase;
        private int? reportedSecondsLeft;
        private IReadOnlyList<LobbyEntry> roster = Array.Empty<LobbyEntry>();

        /// <summary>The players the session expects (the match's session size, or the host's seat count), at least 1.</summary>
        public int ExpectedPlayers { get; private set; } = 1;

        /// <summary>How long the lobby waits for the roster before the game server starts with whoever is present.</summary>
        public TimeSpan LobbyTimeout { get; private set; } = TimeSpan.FromSeconds(30);

        /// <summary>The players connected now, the local one marked.</summary>
        public IReadOnlyList<LobbyEntry> Roster => roster;

        /// <summary>True after <see cref="Join"/> and before <see cref="Leave"/>.</summary>
        public bool InSession => joinedAt.HasValue;

        /// <summary>Why the session ended, once it has (the game server's reason or the client's own).</summary>
        public string EndReason { get; private set; }

        /// <summary>The scores at the end, best first.</summary>
        public IReadOnlyList<LobbyEntry> FinalScores { get; private set; } = Array.Empty<LobbyEntry>();

        /// <summary>The client was admitted.</summary>
        public void Join(DateTimeOffset now, int expectedPlayers, TimeSpan lobbyTimeout)
        {
            joinedAt = now;
            ExpectedPlayers = Math.Max(1, expectedPlayers);
            LobbyTimeout = lobbyTimeout > TimeSpan.Zero ? lobbyTimeout : TimeSpan.FromSeconds(30);
            reportedPhase = null;
            reportedSecondsLeft = null;
            roster = Array.Empty<LobbyEntry>();
            EndReason = null;
            FinalScores = Array.Empty<LobbyEntry>();
        }

        /// <summary>The players spawned now.</summary>
        public void SetRoster(IReadOnlyList<LobbyEntry> entries)
        {
            roster = entries ?? Array.Empty<LobbyEntry>();
        }

        /// <summary>The phase and the whole seconds left in it, as the game server replicates them (its score board).</summary>
        public void SetReportedPhase(SessionView phase, int secondsLeft)
        {
            reportedPhase = phase;
            reportedSecondsLeft = secondsLeft > 0 ? secondsLeft : (int?)null;
        }

        /// <summary>The connection ended; keeps the last scores for the Results screen.</summary>
        public void Leave(string reason)
        {
            if (!joinedAt.HasValue)
            {
                return;
            }

            joinedAt = null;
            EndReason = string.IsNullOrEmpty(reason) ? "disconnected" : reason;
            var scores = new List<LobbyEntry>(roster);
            scores.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.ClientId.CompareTo(b.ClientId));
            FinalScores = scores;
        }

        /// <summary>The screen to show: the replicated phase when there is one; otherwise lobby until the roster is complete or the timer ran out.</summary>
        public SessionView Phase(DateTimeOffset now)
        {
            if (!joinedAt.HasValue)
            {
                return SessionView.Results;
            }

            if (reportedPhase.HasValue)
            {
                return reportedPhase.Value;
            }

            return roster.Count >= ExpectedPlayers || TimerRemaining(now) == TimeSpan.Zero ? SessionView.Match : SessionView.Lobby;
        }

        /// <summary>Time left in the phase: the game server's count when it replicates one, else the client's own lobby timer; zero outside a session.</summary>
        public TimeSpan TimerRemaining(DateTimeOffset now)
        {
            if (!joinedAt.HasValue)
            {
                return TimeSpan.Zero;
            }

            if (reportedSecondsLeft.HasValue)
            {
                return TimeSpan.FromSeconds(reportedSecondsLeft.Value);
            }

            if (reportedPhase.HasValue)
            {
                return TimeSpan.Zero;
            }

            TimeSpan left = joinedAt.Value + LobbyTimeout - now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        /// <summary>The lobby header, for example <c>2 of 4 players, starting in 0:21</c> (no time when the game server runs an unbounded lobby).</summary>
        public string LobbyText(DateTimeOffset now)
        {
            TimeSpan left = TimerRemaining(now);
            return roster.Count + " of " + ExpectedPlayers + " players" + (left > TimeSpan.Zero ? ", starting in " + MatchmakingModel.FormatClock(left) : ", waiting");
        }

        /// <summary>The match header, for example <c>Match: 2:41 left</c>.</summary>
        public string MatchText(DateTimeOffset now)
        {
            TimeSpan left = TimerRemaining(now);
            return left > TimeSpan.Zero ? "Match: " + MatchmakingModel.FormatClock(left) + " left" : "Match";
        }
    }
}
