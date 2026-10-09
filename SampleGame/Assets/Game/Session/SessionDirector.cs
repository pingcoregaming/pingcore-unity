using System;
using System.Collections.Generic;

namespace BeaconRush.Session
{
    /// <summary>
    /// The Beacon Rush session loop, Lobby to Match to Results and back to Lobby, as a pure state
    /// machine: no Unity types, no clock and no I/O. Its host feeds it inputs (an allocation, a player
    /// joining or leaving, a player reaching the score limit, elapsed time, the platform clearing the
    /// allocation) and carries out the <see cref="SessionCommand"/> each input returns.
    /// <list type="bullet">
    /// <item>An allocation opens a session in <see cref="SessionPhase.Lobby"/> and starts the lobby timer. It names
    /// the players to wait for: the sum of the roster's <c>partySize</c>, or none for a backend allocation.</item>
    /// <item>The lobby starts the match once that many players are in (with no roster, the first player):
    /// <see cref="PhaseCause.RosterComplete"/>.</item>
    /// <item>The lobby timeout with someone there starts the match with whoever is present
    /// (<see cref="PhaseCause.LobbyTimeout"/>); with nobody there it ends the session: <see cref="SessionEndReason.LobbyTimeout"/>.</item>
    /// <item>The last player leaving once the match started ends it: <see cref="SessionEndReason.PlayersLeft"/>.</item>
    /// <item>A match moves to Results when a player reaches the score limit (<see cref="OnScoreLimitReached"/>,
    /// <see cref="PhaseCause.ScoreLimit"/>) or after <see cref="SessionSettings.MatchDuration"/> (<see cref="PhaseCause.TimeUp"/>);
    /// Results ends the session after <see cref="SessionSettings.ResultsDuration"/>: <see cref="SessionEndReason.MatchComplete"/>.
    /// Results is always entered, even for a zero duration (which ends it on the next tick), so the host always sees the result.</item>
    /// <item>The end is <see cref="SessionCommandKind.EndSession"/> or <see cref="SessionCommandKind.Shutdown"/>,
    /// as the session's <see cref="SessionSettings.EndMode"/> says. Either way the director is back in an idle Lobby.</item>
    /// <item>The platform clearing the allocation closes the session with no command: the end is already known.</item>
    /// <item>A joiner who claimed the session but never became a player (<see cref="OnClaimedJoinLost"/>) ends a lobby
    /// that nobody is in and nobody else holds a seat for at once (<see cref="SessionEndReason.LobbyTimeout"/>, early),
    /// instead of at the lobby timeout.</item>
    /// <item>Players belong to the open session, by client id. A join while idle is not counted, and the player set
    /// is emptied whenever a session ends, is cleared or is replaced, so the late disconnect of a player from an
    /// earlier session never starts or ends the next one.</item>
    /// </list>
    /// Every phase change bumps <see cref="PhaseVersion"/> and sets <see cref="Cause"/>, so the host can raise one event per phase.
    /// </summary>
    public sealed class SessionDirector
    {
        private readonly HashSet<ulong> players = new HashSet<ulong>();
        private SessionSettings settings = SessionSettings.Default;

        public SessionPhase Phase { get; private set; } = SessionPhase.Lobby;

        /// <summary>Why the current phase was entered.</summary>
        public PhaseCause Cause { get; private set; } = PhaseCause.Allocation;

        /// <summary>Incremented on every phase entry, idle resets included.</summary>
        public long PhaseVersion { get; private set; }

        /// <summary>The allocation of the open session, or null when the game server is idle.</summary>
        public string AllocationId { get; private set; }

        public bool HasSession => AllocationId != null;

        /// <summary>Players of the open session, as reported through joins and leaves.</summary>
        public int Players => players.Count;

        /// <summary>The players the lobby waits for: the roster's party sizes summed, at least 1. 0 when idle.</summary>
        public int ExpectedPlayers { get; private set; }

        /// <summary>The open session was allocated with a roster (a matchmaker match): its lobby waits for those players. False when idle.</summary>
        public bool HasRoster { get; private set; }

        /// <summary>The settings of the open session, or <see cref="SessionSettings.Default"/> when idle.</summary>
        public SessionSettings Settings => settings;

        /// <summary>Time spent in the current phase of the open session.</summary>
        public TimeSpan TimeInPhase { get; private set; } = TimeSpan.Zero;

        /// <summary>True when <paramref name="clientId"/> joined the open session and has not left.</summary>
        public bool IsSessionPlayer(ulong clientId) => players.Contains(clientId);

        /// <summary>The open session's players, in no particular order.</summary>
        public IReadOnlyCollection<ulong> SessionPlayers => players;

        /// <summary>A session with no roster (a backend allocation, or a local session): the first player starts the match.</summary>
        public SessionCommand OnAllocationReceived(string allocationId, SessionSettings sessionSettings) =>
            OnAllocationReceived(allocationId, sessionSettings, 0);

        /// <summary>
        /// A new allocation opens a session that waits for <paramref name="rosterPlayers"/> players (0 or less: the
        /// first player). The same id again is ignored (the watch stream can repeat it); a different id while a
        /// session is open replaces that session, since the platform has moved on, and its players do not carry over.
        /// </summary>
        public SessionCommand OnAllocationReceived(string allocationId, SessionSettings sessionSettings, int rosterPlayers)
        {
            if (string.IsNullOrEmpty(allocationId))
            {
                throw new ArgumentException("an allocation needs an id", nameof(allocationId));
            }

            if (string.Equals(allocationId, AllocationId, StringComparison.Ordinal))
            {
                return SessionCommand.None;
            }

            players.Clear();
            AllocationId = allocationId;
            settings = sessionSettings ?? SessionSettings.Default;
            ExpectedPlayers = Math.Max(1, rosterPlayers);
            HasRoster = rosterPlayers > 0;
            Enter(SessionPhase.Lobby, PhaseCause.Allocation);
            return SessionCommand.None;
        }

        /// <summary>A player connected. Counted only while a session is open; a repeated id counts once.</summary>
        public SessionCommand OnPlayerJoined(ulong clientId)
        {
            if (!HasSession || !players.Add(clientId))
            {
                return SessionCommand.None;
            }

            if (Phase == SessionPhase.Lobby && players.Count >= ExpectedPlayers)
            {
                Enter(SessionPhase.Match, PhaseCause.RosterComplete);
            }

            return SessionCommand.None;
        }

        /// <summary>A player disconnected. A client that is not a player of the open session changes nothing.</summary>
        public SessionCommand OnPlayerLeft(ulong clientId)
        {
            if (!players.Remove(clientId))
            {
                return SessionCommand.None;
            }

            if (HasSession && players.Count == 0 && Phase != SessionPhase.Lobby)
            {
                return End(SessionEndReason.PlayersLeft);
            }

            return SessionCommand.None;
        }

        /// <summary>A player reached the score limit. Moves a running match to Results; anything else is ignored.</summary>
        public SessionCommand OnScoreLimitReached()
        {
            if (!HasSession || Phase != SessionPhase.Match)
            {
                return SessionCommand.None;
            }

            Enter(SessionPhase.Results, PhaseCause.ScoreLimit);
            return SessionCommand.None;
        }

        /// <summary>
        /// A joiner whose approval claimed this session (the solo join's self-allocation) never became a player: it was
        /// refused after the claim, or its connection closed before it completed. With the session still in its lobby,
        /// nobody in it and <paramref name="otherSeatsHeld"/> 0 (no other admitted or pending joiner holds a seat), the
        /// session ends now (<see cref="SessionEndReason.LobbyTimeout"/>) so the game server is free again at once.
        /// Anything else changes nothing; the host calls it only for its own self-allocated session.
        /// </summary>
        public SessionCommand OnClaimedJoinLost(int otherSeatsHeld)
        {
            if (!HasSession || Phase != SessionPhase.Lobby || players.Count > 0 || otherSeatsHeld > 0)
            {
                return SessionCommand.None;
            }

            return End(SessionEndReason.LobbyTimeout);
        }

        /// <summary>The platform cleared the allocation without the game ending it: close the session, ask for nothing.</summary>
        public SessionCommand OnAllocationCleared()
        {
            Reset();
            return SessionCommand.None;
        }

        /// <summary>Advances the clock of the open session. A zero or negative step changes nothing.</summary>
        public SessionCommand Tick(TimeSpan elapsed)
        {
            if (!HasSession || elapsed <= TimeSpan.Zero)
            {
                return SessionCommand.None;
            }

            TimeInPhase += elapsed;
            switch (Phase)
            {
                case SessionPhase.Lobby:
                    if (TimeInPhase < settings.LobbyTimeout)
                    {
                        return SessionCommand.None;
                    }

                    if (players.Count == 0)
                    {
                        return End(SessionEndReason.LobbyTimeout);
                    }

                    Enter(SessionPhase.Match, PhaseCause.LobbyTimeout);
                    return SessionCommand.None;
                case SessionPhase.Match:
                    if (TimeInPhase < settings.MatchDuration)
                    {
                        return SessionCommand.None;
                    }

                    Enter(SessionPhase.Results, PhaseCause.TimeUp);
                    return SessionCommand.None;
                case SessionPhase.Results:
                    return TimeInPhase >= settings.ResultsDuration ? End(SessionEndReason.MatchComplete) : SessionCommand.None;
                default:
                    return SessionCommand.None;
            }
        }

        /// <summary>Time left in the current phase of the open session, or zero when idle or past due.</summary>
        public TimeSpan TimeLeft
        {
            get
            {
                if (!HasSession)
                {
                    return TimeSpan.Zero;
                }

                TimeSpan length = Phase == SessionPhase.Lobby ? settings.LobbyTimeout
                    : Phase == SessionPhase.Match ? settings.MatchDuration
                    : settings.ResultsDuration;
                TimeSpan left = length - TimeInPhase;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        private SessionCommand End(SessionEndReason reason)
        {
            SessionCommandKind kind = settings.EndMode == SessionEndMode.Shutdown ? SessionCommandKind.Shutdown : SessionCommandKind.EndSession;
            var command = new SessionCommand(kind, AllocationId, reason);
            Reset();
            return command;
        }

        private void Reset()
        {
            players.Clear();
            AllocationId = null;
            ExpectedPlayers = 0;
            HasRoster = false;
            settings = SessionSettings.Default;
            Enter(SessionPhase.Lobby, PhaseCause.Allocation);
        }

        private void Enter(SessionPhase phase, PhaseCause cause)
        {
            Phase = phase;
            Cause = cause;
            TimeInPhase = TimeSpan.Zero;
            PhaseVersion++;
        }
    }
}
