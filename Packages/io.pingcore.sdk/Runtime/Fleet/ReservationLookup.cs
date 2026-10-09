using System.Collections.Generic;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>What <see cref="IFleetSdk.GetReservationAsync"/> found.</summary>
    public enum ReservationLookupStatus
    {
        /// <summary>A live reservation on this game server. Admit by <see cref="LocalReservation.PlayerIds"/> and <see cref="LocalReservation.Seats"/>.</summary>
        Found = 0,

        /// <summary>The endpoint still answered <c>reservation not found</c> after the wait window: unknown, released or expired. Refuse the join.</summary>
        NotFound = 1,

        /// <summary>The endpoint served the record, but its <c>expiresAt</c> is not after the scheduler's now. Refuse the join.</summary>
        Expired = 2,

        /// <summary>The endpoint did not answer (a transport failure, or a refused connection while stopping).</summary>
        Unreachable = 3,

        /// <summary>This supervisor has no reservation routes (a JSON 501, or a non-JSON 404 from a supervisor before 1.3.4).</summary>
        Unsupported = 4,

        /// <summary>Any other answer: another status, a 404 that is not the supervisor's, an unparseable body or a record for another id.</summary>
        Error = 5,

        /// <summary>Not a hosted game server; nothing was sent.</summary>
        Inert = 6,

        /// <summary>The caller's cancellation token fired.</summary>
        Cancelled = 7,
    }

    /// <summary>The result of one hosted reservation lookup.</summary>
    public sealed class ReservationLookup
    {
        /// <summary>Creates a lookup result.</summary>
        public ReservationLookup(ReservationLookupStatus status, LocalReservation reservation, int httpStatus, string message)
        {
            Status = status;
            Reservation = reservation;
            HttpStatus = httpStatus;
            Message = message;
        }

        /// <summary>What was found.</summary>
        public ReservationLookupStatus Status { get; }

        /// <summary>The record for <see cref="ReservationLookupStatus.Found"/> and <see cref="ReservationLookupStatus.Expired"/>; otherwise null.</summary>
        public LocalReservation Reservation { get; }

        /// <summary>The HTTP status of the last answer, or 0.</summary>
        public int HttpStatus { get; }

        /// <summary>Why, for anything but <see cref="ReservationLookupStatus.Found"/>; null otherwise.</summary>
        public string Message { get; }

        /// <summary>True for <see cref="ReservationLookupStatus.Found"/>.</summary>
        public bool IsFound => Status == ReservationLookupStatus.Found;
    }

    /// <summary>The result of <see cref="IFleetSdk.ListReservationsAsync"/>.</summary>
    public sealed class ReservationsResult : FleetCallResult
    {
        /// <summary>Creates a list result.</summary>
        public ReservationsResult(FleetCallOutcome outcome, int status, string message, IReadOnlyList<LocalReservation> reservations)
            : base(outcome, status, message)
        {
            Reservations = reservations ?? new List<LocalReservation>();
        }

        /// <summary>Every live reservation on this game server, soonest-expiring first; empty when the call failed. Never null.</summary>
        public IReadOnlyList<LocalReservation> Reservations { get; }
    }
}
