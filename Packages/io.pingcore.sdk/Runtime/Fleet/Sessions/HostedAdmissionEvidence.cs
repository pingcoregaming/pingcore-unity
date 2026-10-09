using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core.Handshake;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Sessions
{
    /// <summary>
    /// The admission evidence of a PingCore-hosted game server, all read from the local SDK endpoint
    /// with GETs (so nothing integrates the game, and verify is never called):
    /// <list type="bullet">
    /// <item><c>reservation</c>: <see cref="IFleetSdk.GetReservationAsync"/>, which already looks again for
    /// up to 2 s for a hold whose push trails the player.</item>
    /// <item><c>match</c>: <see cref="IFleetSdk.CurrentAllocation"/> and its roster (<see cref="MatchContext"/>),
    /// and whether it is a supervisor self-allocation (<see cref="AllocationInfo.IsSelfAllocated"/>, rosterless
    /// only with <see cref="ApprovalOptions.AllowSelfAllocatedJoins"/>); for an allocation with no roster
    /// (<see cref="MatchContext.HasRoster"/> false, never for a context that did not parse), the player cap:
    /// the <c>players</c> counter's capacity when the view has one above 0, else the
    /// <see cref="ApprovalOptions.MaxPlayers"/> already in the facts (0 is no limit).</item>
    /// <item><c>backfill</c>: the delivered backfill from <see cref="BackfillWatcher"/>, waiting up to
    /// <see cref="ApprovalOptions.BackfillWait"/> (polling every <see cref="ApprovalOptions.BackfillPoll"/>)
    /// because the joiner can outrun the annotation; plus the current allocation it must join.</item>
    /// </list>
    /// It is also the session claim of the hosted reservation rule (<see cref="ISessionClaimEvidence"/>), used only when the
    /// game set <see cref="ApprovalOptions.ClaimIdleSessions"/>: a <c>reservation</c> join the table and the gate accepted on an
    /// idle game server is then approved only after <see cref="IFleetSdk.AllocateSelfAsync"/> (a write, the one this evidence
    /// makes) put the game server <c>in_session</c>, so the matchmaker skips it, and the game must end that session itself.
    /// See <see cref="ClaimSessionAsync"/>.
    /// </summary>
    public sealed class HostedAdmissionEvidence : IAdmissionEvidence, ISessionClaimEvidence
    {
        private readonly IFleetSdk fleet;
        private readonly BackfillWatcher backfills;

        /// <summary>Creates the evidence.</summary>
        /// <param name="fleet">The local SDK shim.</param>
        /// <param name="backfills">The game server's backfill watcher; null refuses every <c>backfill</c> ticket as <c>backfill_unknown</c>.</param>
        public HostedAdmissionEvidence(IFleetSdk fleet, BackfillWatcher backfills)
        {
            this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            this.backfills = backfills;
        }

        /// <inheritdoc />
        public string Source => "fleet";

        /// <inheritdoc />
        public bool IsStopping => fleet.State == FleetState.Stopping || fleet.State == FleetState.ShuttingDown;

        /// <inheritdoc />
        public async Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            switch (ticket.Kind)
            {
                case JoinTicketKind.Reservation:
                    await GatherReservation(ticket, facts, cancellationToken);
                    break;

                case JoinTicketKind.Match:
                {
                    AllocationInfo allocation = fleet.CurrentAllocation;
                    MatchContext match = MatchContext.Parse(allocation);
                    facts.CurrentAllocationId = match?.AllocationId;
                    facts.Roster = match?.Roster;
                    facts.RosterlessAllocation = match != null && !match.HasRoster;
                    facts.SelfAllocation = allocation != null && allocation.IsSelfAllocated;
                    if (facts.RosterlessAllocation)
                    {
                        facts.MaxPlayers = PlayerCap(fleet.Current, facts.MaxPlayers);
                    }

                    break;
                }

                case JoinTicketKind.Backfill:
                    await GatherBackfill(ticket, facts, options, cancellationToken);
                    break;
            }

            facts.Stopping = facts.Stopping || IsStopping;
        }

        /// <summary>
        /// Claims the idle game server for the session a <c>reservation</c> join starts:
        /// <see cref="IFleetSdk.AllocateSelfAsync"/>, shared by concurrent joiners, then <see cref="MapSelfAllocation"/>.
        /// </summary>
        public async Task<SessionClaimResult> ClaimSessionAsync(JoinTicket ticket, AdmissionFacts facts, CancellationToken cancellationToken)
        {
            SelfAllocationResult result = await fleet.AllocateSelfAsync(cancellationToken);
            return MapSelfAllocation(result);
        }

        /// <summary>
        /// What a self-allocation means for the joiner, pure: confirmed, or an allocation that was already current and is a
        /// self-allocation (the game server claimed itself for an earlier joiner) is <see cref="SessionClaimOutcome.Claimed"/>;
        /// an allocation that was already current and is the platform's (a match or a backend allocation that landed
        /// between the hold and the claim) is <see cref="SessionClaimOutcome.AllocatedMeanwhile"/>, for the game's gate to
        /// decide; anything else (refused, unreachable, cancelled, inert, or a 2xx whose frame never came) is
        /// <see cref="SessionClaimOutcome.Failed"/>.
        /// </summary>
        public static SessionClaimResult MapSelfAllocation(SelfAllocationResult result)
        {
            if (result == null)
            {
                return SessionClaimResult.Failed("no answer");
            }

            if (result.IsConfirmed)
            {
                return SessionClaimResult.Claimed(result.Allocation.AllocationId);
            }

            if (result.AlreadyAllocated && result.Allocation != null)
            {
                return result.Allocation.IsSelfAllocated
                    ? SessionClaimResult.Claimed(result.Allocation.AllocationId)
                    : SessionClaimResult.AllocatedMeanwhile(result.Allocation.AllocationId);
            }

            return SessionClaimResult.Failed("self-allocation " + result + (result.Message != null ? ": " + result.Message : string.Empty));
        }

        /// <summary>
        /// The cap of a rosterless allocation: the <c>players</c> counter's capacity on
        /// <paramref name="view"/> when it has one above 0 (clamped to <see cref="int.MaxValue"/>), else
        /// <paramref name="fallback"/>. Pure.
        /// </summary>
        public static int PlayerCap(GameServerSnapshot view, int fallback)
        {
            if (view?.Counters != null && view.Counters.TryGetValue("players", out GameServerCounter players) && players?.Capacity > 0)
            {
                return players.Capacity.Value > int.MaxValue ? int.MaxValue : (int)players.Capacity.Value;
            }

            return fallback;
        }

        /// <summary>Maps a hosted hold lookup onto the decision's evidence. Pure.</summary>
        public static ReservationEvidence MapLookup(ReservationLookupStatus status)
        {
            switch (status)
            {
                case ReservationLookupStatus.Found:
                    return ReservationEvidence.HoldFound;
                case ReservationLookupStatus.NotFound:
                    return ReservationEvidence.HoldNotFound;
                case ReservationLookupStatus.Expired:
                    return ReservationEvidence.HoldExpired;
                case ReservationLookupStatus.Unreachable:
                case ReservationLookupStatus.Cancelled:
                    return ReservationEvidence.HoldUnreachable;
                default:
                    // Unsupported, Error, Inert: no hold can be read here.
                    return ReservationEvidence.HoldError;
            }
        }

        private async Task GatherReservation(JoinTicket ticket, AdmissionFacts facts, CancellationToken cancellationToken)
        {
            ReservationLookup lookup = await fleet.GetReservationAsync(ticket.ReservationId, cancellationToken);
            facts.Reservation = MapLookup(lookup.Status);
            LocalReservation hold = lookup.Reservation;
            if (lookup.IsFound && hold != null)
            {
                facts.ReservationSeats = hold.Seats;
                facts.ReservationPlayerIds = hold.PlayerIds;
                facts.ReservationContext = hold.Context;
            }

            facts.CurrentAllocationId = fleet.CurrentAllocation?.AllocationId;
        }

        private async Task GatherBackfill(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken)
        {
            BackfillContext backfill = null;
            if (backfills != null)
            {
                TimeSpan wait = options?.BackfillWait ?? TimeSpan.FromSeconds(5);
                TimeSpan poll = options?.BackfillPoll ?? TimeSpan.FromMilliseconds(500);
                backfill = await backfills.WaitForAsync(ticket.AllocationId, wait, poll, cancellationToken);
            }

            facts.CurrentAllocationId = fleet.CurrentAllocation?.AllocationId;
            facts.BackfillDelivered = backfill != null;
            facts.BackfillAllocationId = backfill?.AllocationId;
            facts.BackfillSessionId = backfill?.SessionId;
            facts.Roster = backfill?.Roster;
        }
    }
}
