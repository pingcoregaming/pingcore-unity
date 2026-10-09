using System;

namespace BeaconRush.Session
{
    /// <summary>
    /// The timings, end mode and switches of one session. <see cref="Default"/> is what an allocation gets and
    /// <see cref="Local"/> what a local session gets; the installed <see cref="Hosting.ServerInstrumentation"/> may hand
    /// back others. Immutable: every <c>With</c> returns a copy.
    /// </summary>
    public sealed class SessionSettings
    {
        public static readonly TimeSpan DefaultLobbyTimeout = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan DefaultMatchDuration = TimeSpan.FromMinutes(3);
        public static readonly TimeSpan DefaultResultsDuration = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The lobby of a self-allocated session (a quick-play solo join claimed the game server). Its player is let in right
        /// after the claim, and the first player starts the match, so this only bounds a claim whose player never arrived
        /// (a joiner the runtime saw drop ends the lobby at once, <see cref="SessionDirector.OnClaimedJoinLost"/>; this catches a
        /// claim that landed after its joiner was already refused). Short, because until it ends the game server is
        /// <c>in_session</c> and the matchmaker skips it.
        /// </summary>
        public static readonly TimeSpan SelfAllocatedLobby = TimeSpan.FromSeconds(10);

        /// <summary>A lobby that waits for its first player as long as it takes: the local sessions of a self-hosted, unlisted or listen game server.</summary>
        public static readonly TimeSpan UnboundedLobby = TimeSpan.FromDays(3650);

        public SessionSettings(TimeSpan lobbyTimeout, TimeSpan matchDuration, TimeSpan resultsDuration, SessionEndMode endMode)
            : this(lobbyTimeout, matchDuration, resultsDuration, endMode, false, null, false)
        {
        }

        private SessionSettings(TimeSpan lobbyTimeout, TimeSpan matchDuration, TimeSpan resultsDuration, SessionEndMode endMode,
            bool joinInProgress, int? joinableOpenSeats, bool bots)
        {
            if (lobbyTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(lobbyTimeout), "the lobby timeout must be positive");
            }

            if (matchDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(matchDuration), "the match duration must be positive");
            }

            if (resultsDuration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(resultsDuration), "the results duration must not be negative");
            }

            if (joinableOpenSeats < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(joinableOpenSeats), "open seats must not be negative");
            }

            LobbyTimeout = lobbyTimeout;
            MatchDuration = matchDuration;
            ResultsDuration = resultsDuration;
            EndMode = endMode;
            JoinInProgress = joinInProgress;
            JoinableOpenSeats = joinableOpenSeats;
            Bots = bots;
        }

        public static SessionSettings Default { get; } =
            new SessionSettings(DefaultLobbyTimeout, DefaultMatchDuration, DefaultResultsDuration, SessionEndMode.EndSession);

        /// <summary>The settings of a local session: the default match, a lobby that waits for its first player as long as it takes.</summary>
        public static SessionSettings Local { get; } = Default.WithLobbyTimeout(UnboundedLobby);

        /// <summary>How long the lobby waits for the roster after an allocation. Nobody there ends the session; anybody there starts the match.</summary>
        public TimeSpan LobbyTimeout { get; }

        /// <summary>How long a match runs before the leader wins.</summary>
        public TimeSpan MatchDuration { get; }

        /// <summary>How long the results phase shows before the session ends.</summary>
        public TimeSpan ResultsDuration { get; }

        public SessionEndMode EndMode { get; }

        /// <summary>Hosted: publish a joinable record at match start, so the matchmaker can backfill the session.</summary>
        public bool JoinInProgress { get; }

        /// <summary>
        /// Hosted, with <see cref="JoinInProgress"/>: publish once with exactly this many open seats, as given, instead of
        /// keeping the record in step with the seats; null (the default) keeps it in step.
        /// </summary>
        public int? JoinableOpenSeats { get; }

        /// <summary>The game server steers every player to the nearest beacon itself (bots), so a match between headless clients can finish. Off by default.</summary>
        public bool Bots { get; }

        public SessionSettings WithLobbyTimeout(TimeSpan lobbyTimeout) =>
            new SessionSettings(lobbyTimeout, MatchDuration, ResultsDuration, EndMode, JoinInProgress, JoinableOpenSeats, Bots);

        public SessionSettings WithMatchDuration(TimeSpan matchDuration) =>
            new SessionSettings(LobbyTimeout, matchDuration, ResultsDuration, EndMode, JoinInProgress, JoinableOpenSeats, Bots);

        public SessionSettings WithResultsDuration(TimeSpan resultsDuration) =>
            new SessionSettings(LobbyTimeout, MatchDuration, resultsDuration, EndMode, JoinInProgress, JoinableOpenSeats, Bots);

        public SessionSettings WithEndMode(SessionEndMode endMode) =>
            new SessionSettings(LobbyTimeout, MatchDuration, ResultsDuration, endMode, JoinInProgress, JoinableOpenSeats, Bots);

        public SessionSettings WithJoinInProgress(bool joinInProgress) =>
            new SessionSettings(LobbyTimeout, MatchDuration, ResultsDuration, EndMode, joinInProgress, JoinableOpenSeats, Bots);

        public SessionSettings WithJoinableOpenSeats(int? joinableOpenSeats) =>
            new SessionSettings(LobbyTimeout, MatchDuration, ResultsDuration, EndMode, JoinInProgress, joinableOpenSeats, Bots);

        public SessionSettings WithBots(bool bots) =>
            new SessionSettings(LobbyTimeout, MatchDuration, ResultsDuration, EndMode, JoinInProgress, JoinableOpenSeats, bots);
    }
}
