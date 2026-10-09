using System;
using System.Collections.Generic;
using BeaconRush.Hosting;
using BeaconRush.Networking;
using BeaconRush.Session;
using PingCore.Core.Handshake;

namespace BeaconRush.Admission
{
    /// <summary>What <see cref="BeaconRushAdmissionGate"/> knows about the game server when it is asked. A plain snapshot, so the table is tested without a game server.</summary>
    public readonly struct GateState
    {
        public GateState(GameHostingMode mode, bool sessionOpen, SessionPhase phase, int seatsTaken, bool sessionHasRoster = false)
        {
            Mode = mode;
            SessionOpen = sessionOpen;
            Phase = phase;
            SeatsTaken = seatsTaken;
            SessionHasRoster = sessionHasRoster;
        }

        public GameHostingMode Mode { get; }

        /// <summary>
        /// Hosted: the director has a session for the shim's current allocation. Every other mode: the local session
        /// is open (it always is, except for the frame between one local session and the next).
        /// </summary>
        public bool SessionOpen { get; }

        public SessionPhase Phase { get; }

        /// <summary>Seats already held: admitted connections in the SDK's ledger, plus a listen host's own player.</summary>
        public int SeatsTaken { get; }

        /// <summary>Hosted: the open session was allocated with a roster (a matchmaker match), so its lobby waits for those players.</summary>
        public bool SessionHasRoster { get; }
    }

    /// <summary>
    /// Beacon Rush's own admission rule (<see cref="IAdmissionGate"/>), asked after the SDK's decision table accepted a
    /// ticket. Checked in order:
    /// <list type="number">
    /// <item>The platform allocated this game server between a quick-play hold and its session claim
    /// (<see cref="SessionClaimOutcome.AllocatedMeanwhile"/>, the gate's second say): <c>refused_by_game</c>; the client
    /// tries quick play once more.</item>
    /// <item>No open session: <c>not_in_session</c>, except a hosted <c>reservation</c> on an idle game server (no
    /// current allocation): the solo join. It is admitted, and the SDK's pipeline claims a session for it (a
    /// self-allocation, so the matchmaker skips this game server) before the approval; the session then starts the
    /// match with its first player. A <c>match</c> or <c>backfill</c> ticket whose session just ended, and a reservation
    /// while an allocation is current but its session is not open here (just ended, not yet cleared), stay refused.</item>
    /// <item>The session is in <c>results</c>, about to end: <c>refused_by_game</c>.</item>
    /// <item>A hosted <c>reservation</c> into the lobby of a session allocated with a roster (a match waiting for its
    /// matched players): <c>refused_by_game</c>. A reservation joiner is not on the roster, and counting it would start
    /// the match before the matched players arrive. Quick join ranks the fullest game server first, so a quick-play retry
    /// can well land a hold here. A reservation into a rosterless lobby (a backend allocation, a self-allocated solo
    /// session) or into a running match is still admitted, and so are <c>match</c> and <c>backfill</c> tickets.</item>
    /// <item><see cref="BeaconRushProtocol.MaxPlayers"/> seats taken: <c>server_full</c>.</item>
    /// <item>Otherwise admit.</item>
    /// </list>
    /// An admitted ticket's display name is kept (by player id) for the score board; the runtime takes it back with
    /// <see cref="TakeDisplayName"/> once the approval is delivered.
    /// </summary>
    public sealed class BeaconRushAdmissionGate : IAdmissionGate
    {
        private readonly Func<GateState> state;
        private readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="state">Read on the main thread for every ticket the table accepted.</param>
        public BeaconRushAdmissionGate(Func<GateState> state)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>The verdict for <paramref name="ticket"/> in <paramref name="current"/>, with no evidence facts (an idle hosted game server then admits nothing). Pure.</summary>
        public static AdmissionGateResult Decide(JoinTicket ticket, GateState current) => Decide(ticket, current, null);

        /// <summary>The verdict for <paramref name="ticket"/> in <paramref name="current"/>, given what the evidence found. Pure.</summary>
        public static AdmissionGateResult Decide(JoinTicket ticket, GateState current, AdmissionFacts facts)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (facts != null && facts.SessionClaim == SessionClaimOutcome.AllocatedMeanwhile)
            {
                return AdmissionGateResult.Reject(JoinRejectReason.RefusedByGame,
                    "this game server was just allocated to a match; a quick-play player is not seated in its lobby");
            }

            if (!current.SessionOpen && !IsSoloJoin(ticket, current, facts))
            {
                return AdmissionGateResult.Reject(JoinRejectReason.NotInSession, current.Mode == GameHostingMode.Hosted
                    ? "this game server has no open session"
                    : "no local session is open");
            }

            if (current.Phase == SessionPhase.Results)
            {
                return AdmissionGateResult.Reject(JoinRejectReason.RefusedByGame, "the match is over; the session is about to end");
            }

            if (current.Mode == GameHostingMode.Hosted && ticket.Kind == JoinTicketKind.Reservation && current.SessionOpen
                && current.SessionHasRoster && current.Phase == SessionPhase.Lobby)
            {
                return AdmissionGateResult.Reject(JoinRejectReason.RefusedByGame, "the lobby waits for its matched players; a reservation joiner is not seated in it");
            }

            if (current.SeatsTaken >= BeaconRushProtocol.MaxPlayers)
            {
                return AdmissionGateResult.Reject(JoinRejectReason.ServerFull, "all " + BeaconRushProtocol.MaxPlayers + " seats are taken");
            }

            return AdmissionGateResult.Admit;
        }

        /// <summary>
        /// The solo join: a hosted <c>reservation</c> whose evidence found no current allocation, before any session claim.
        /// The SDK pipeline claims a session for it (<see cref="JoinAdmission.NeedsSessionClaim"/>) before the approval.
        /// </summary>
        public static bool IsSoloJoin(JoinTicket ticket, GateState current, AdmissionFacts facts)
        {
            return current.Mode == GameHostingMode.Hosted && ticket != null && facts != null && JoinAdmission.NeedsSessionClaim(ticket, facts);
        }

        /// <inheritdoc />
        public AdmissionGateResult CanAdmit(JoinTicket ticket, AdmissionFacts facts)
        {
            AdmissionGateResult verdict = Decide(ticket, state(), facts);
            if (verdict.Admitted && ticket.PlayerId != null)
            {
                names[ticket.PlayerId] = ticket.DisplayName;
            }

            return verdict;
        }

        /// <summary>The display name the admitted ticket of <paramref name="playerId"/> carried (or null), forgotten afterwards.</summary>
        public string TakeDisplayName(string playerId)
        {
            if (playerId == null || !names.TryGetValue(playerId, out string name))
            {
                return null;
            }

            names.Remove(playerId);
            return name;
        }
    }
}
