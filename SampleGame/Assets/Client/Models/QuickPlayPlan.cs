namespace BeaconRush.Client.Models
{
    /// <summary>What Quick play does next.</summary>
    public enum QuickPlayStep
    {
        /// <summary>Connect to the game server the quick join held a seat on, with a <c>reservation</c> join ticket.</summary>
        Connect,

        /// <summary>No game server had a seat: queue on Find match instead, with <see cref="QuickPlayPlan.FindMatchNote"/>.</summary>
        FindMatchInstead,

        /// <summary>The game server was taken between the hold and the join: quick join once more, with <see cref="QuickPlayPlan.RetryNote"/>.</summary>
        RetryQuickPlay,

        /// <summary>Give up and say why.</summary>
        Fail,
    }

    /// <summary>
    /// Quick play's decisions, pure. Quick play quick-joins the fleet app, which holds a seat on a fleet game server with
    /// room (an idle one included: Beacon Rush admits a lone player into an idle hosted game server, which claims itself
    /// for that session before letting the player in), then connects with a <c>reservation</c> join ticket.
    /// <list type="bullet">
    /// <item>The quick join answers <c>no_seats</c> (no game server has a seat, or none within the latency limit): queue on
    /// Find match instead, where a match starts once a second player is queued.</item>
    /// <item>The game server refuses the join with <c>refused_by_game</c>, <c>not_in_session</c> or <c>server_full</c>:
    /// it was taken between the hold and the join (a match landed on it, its session ended, or it filled up). Quick join
    /// once more; a second refusal is final.</item>
    /// </list>
    /// </summary>
    public static class QuickPlayPlan
    {
        /// <summary>Quick joins one press of Quick play makes at most.</summary>
        public const int MaxAttempts = 2;

        /// <summary>The status line when Quick play falls back to Find match.</summary>
        public const string FindMatchNote =
            "No game server is free for a quick game right now, so you are queued for a match instead: waiting for a second player.";

        /// <summary>The status line when Quick play tries again.</summary>
        public const string RetryNote = "That game server was just taken. Retrying quick play...";

        /// <summary>After the quick join: connect, fall back to Find match, or fail.</summary>
        /// <param name="usable">The quick join answered 200 with an address, a port and a player id to put in the ticket.</param>
        /// <param name="reasonWire">The refusal's reason literal, or null.</param>
        public static QuickPlayStep AfterQuickJoin(bool usable, string reasonWire)
        {
            if (usable)
            {
                return QuickPlayStep.Connect;
            }

            return reasonWire == "no_seats" ? QuickPlayStep.FindMatchInstead : QuickPlayStep.Fail;
        }

        /// <summary>After the game server refused the join with <paramref name="literal"/> on quick join number <paramref name="attempt"/> (from 1).</summary>
        public static QuickPlayStep AfterRefusal(string literal, int attempt)
        {
            bool taken = literal == "refused_by_game" || literal == "not_in_session" || literal == "server_full";
            return taken && attempt < MaxAttempts ? QuickPlayStep.RetryQuickPlay : QuickPlayStep.Fail;
        }
    }
}
