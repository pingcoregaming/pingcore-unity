namespace BeaconRush.Session
{
    /// <summary>The phases of a Beacon Rush session. A game server with no allocation idles in <see cref="Lobby"/>.</summary>
    public enum SessionPhase
    {
        Lobby,
        Match,
        Results,
    }

    /// <summary>
    /// How a session ends. <see cref="EndSession"/> (the default, flow F4 variant A) reports the end on
    /// the local SDK endpoint and resets in process; <see cref="Shutdown"/> (variant B) asks the
    /// supervisor to recycle the game process instead.
    /// </summary>
    public enum SessionEndMode
    {
        EndSession,
        Shutdown,
    }

    /// <summary>The literal names of <see cref="SessionEndMode"/> used in events.</summary>
    public static class SessionEndModes
    {
        public const string EndSession = "endSession";
        public const string Shutdown = "shutdown";

        /// <summary><c>shutdown</c> or <c>endSession</c>.</summary>
        public static string ToWire(SessionEndMode mode) => mode == SessionEndMode.Shutdown ? Shutdown : EndSession;
    }

    /// <summary>Why a session ended. <see cref="SessionEndReasons.ToWire"/> gives the event literal.</summary>
    public enum SessionEndReason
    {
        /// <summary>No player arrived within the lobby timeout after the allocation.</summary>
        LobbyTimeout,

        /// <summary>The last player left after the match started.</summary>
        PlayersLeft,

        /// <summary>The match ran its course and the results phase finished.</summary>
        MatchComplete,
    }

    /// <summary>Why the session entered its current phase. <see cref="SessionEndReasons.ToWire(PhaseCause)"/> gives the event literal.</summary>
    public enum PhaseCause
    {
        /// <summary>An allocation opened the session (its lobby).</summary>
        Allocation,

        /// <summary>The roster arrived in full (or, with no roster, the first player): the match starts.</summary>
        RosterComplete,

        /// <summary>The lobby timed out with someone there: the match starts with whoever is present.</summary>
        LobbyTimeout,

        /// <summary>A player reached the score limit: results.</summary>
        ScoreLimit,

        /// <summary>The match time ran out: results, the leader wins.</summary>
        TimeUp,
    }

    /// <summary>The literal names of <see cref="SessionEndReason"/> and <see cref="PhaseCause"/> used in events.</summary>
    public static class SessionEndReasons
    {
        public static string ToWire(SessionEndReason reason)
        {
            switch (reason)
            {
                case SessionEndReason.LobbyTimeout:
                    return "lobby_timeout";
                case SessionEndReason.PlayersLeft:
                    return "players_left";
                case SessionEndReason.MatchComplete:
                    return "match_complete";
                default:
                    return "unknown";
            }
        }

        public static string ToWire(PhaseCause cause)
        {
            switch (cause)
            {
                case PhaseCause.Allocation:
                    return "allocation";
                case PhaseCause.RosterComplete:
                    return "roster_complete";
                case PhaseCause.LobbyTimeout:
                    return "lobby_timeout";
                case PhaseCause.ScoreLimit:
                    return "score_limit";
                case PhaseCause.TimeUp:
                    return "time_up";
                default:
                    return "unknown";
            }
        }

        /// <summary>The <c>phase</c> literal: <c>lobby</c>, <c>match</c> or <c>results</c>.</summary>
        public static string ToWire(SessionPhase phase)
        {
            switch (phase)
            {
                case SessionPhase.Lobby:
                    return "lobby";
                case SessionPhase.Match:
                    return "match";
                case SessionPhase.Results:
                    return "results";
                default:
                    return "unknown";
            }
        }
    }
}
