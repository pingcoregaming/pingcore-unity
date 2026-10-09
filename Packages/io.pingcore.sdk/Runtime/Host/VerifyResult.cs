using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Discovery.Host
{
    /// <summary>What a reservation verify decided. Admit the player only on <see cref="Valid"/>.</summary>
    public enum VerifyVerdict
    {
        /// <summary>The hold is live on this game server and admits the player.</summary>
        Valid = 0,

        /// <summary>No live hold: unknown, expired, released or malformed id, a verdict-only <c>valid: false</c>, or a request the SDK refused to send (no player id).</summary>
        Invalid = 1,

        /// <summary>The hold is live on another game server (<c>reason: wrong_server</c>, or a detailed answer naming another <c>serverId</c>).</summary>
        WrongServer = 2,

        /// <summary>The hold does not name the player (<c>reason: not_in_reservation</c>, or detailed <c>playerIds</c> without the player).</summary>
        NotInReservation = 3,

        /// <summary>Discovery could not answer (429, 503, other failures, no answer, cancelled) or no heartbeat was accepted yet. Never treat as valid.</summary>
        Unavailable = 4,
    }

    /// <summary>The result of <see cref="HeartbeatReporter.VerifyReservationAsync"/>.</summary>
    public sealed class VerifyResult
    {
        /// <summary>Creates a result.</summary>
        public VerifyResult(VerifyVerdict verdict, bool detailed, VerifyReservationResponse response, DiscoveryCallResult call)
        {
            Verdict = verdict;
            Detailed = detailed;
            Response = response;
            Call = call;
        }

        /// <summary>The verdict.</summary>
        public VerifyVerdict Verdict { get; }

        /// <summary>
        /// True when Discovery sent the detailed answer (a private app's token): a <c>reason</c> or any
        /// hold field beside <c>valid</c>. False for the verdict-only answer an open app's shipped
        /// token gets, where Discovery itself admitted the player to the seat.
        /// </summary>
        public bool Detailed { get; }

        /// <summary>The answer, or null when there was none.</summary>
        public VerifyReservationResponse Response { get; }

        /// <summary>The call result (a local refusal with status 0 when nothing was sent).</summary>
        public DiscoveryCallResult Call { get; }

        /// <summary>True only for <see cref="VerifyVerdict.Valid"/>.</summary>
        public bool IsValid => Verdict == VerifyVerdict.Valid;

        /// <inheritdoc />
        public override string ToString() => Verdict + (Detailed ? " (detailed)" : " (verdict only)") + (Call == null ? string.Empty : " " + Call);
    }
}
