using System;
using Newtonsoft.Json.Linq;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>The verdict on one lookup answer.</summary>
    internal sealed class ReservationVerdict
    {
        public ReservationVerdict(ReservationLookupStatus status, LocalReservation reservation, bool retry, string message)
        {
            Status = status;
            Reservation = reservation;
            Retry = retry;
            Message = message;
        }

        public ReservationLookupStatus Status { get; }

        public LocalReservation Reservation { get; }

        /// <summary>True for the supervisor's own <c>reservation not found</c>: worth asking again inside the wait window.</summary>
        public bool Retry { get; }

        public string Message { get; }
    }

    /// <summary>
    /// Classifies one <c>GET /pingcore/reservations/{id}</c> answer, pure. Mirrors the fleet
    /// probe's <c>lookupResult</c> and <c>pushedDecision</c>:
    /// transport failure is Unreachable; a JSON 404 <c>reservation not found</c> is NotFound
    /// (retryable); a non-JSON 404 or a 501 is Unsupported (a supervisor without the routes);
    /// any other 404 or status, an unparseable body or a record for another id is Error; a
    /// record whose <c>expiresAt</c> is not after now is Expired; otherwise Found.
    /// </summary>
    internal static class ReservationLookupPolicy
    {
        public static ReservationVerdict Classify(FleetCallOutcome outcome, int status, string body, string requestedId, DateTimeOffset now)
        {
            switch (outcome)
            {
                case FleetCallOutcome.Cancelled:
                    return new ReservationVerdict(ReservationLookupStatus.Cancelled, null, false, "cancelled");
                case FleetCallOutcome.Inert:
                    return new ReservationVerdict(ReservationLookupStatus.Inert, null, false, "not a hosted game server");
                case FleetCallOutcome.Unreachable:
                case FleetCallOutcome.EndpointClosed:
                    return new ReservationVerdict(ReservationLookupStatus.Unreachable, null, false, "the local SDK endpoint did not answer");
            }

            if (status == 404)
            {
                JObject json = LocalSdkValues.TryParseObject(body);
                if (json == null)
                {
                    return new ReservationVerdict(ReservationLookupStatus.Unsupported, null, false, "the local SDK endpoint answered a non-JSON 404: no reservation routes (a supervisor before 1.3.0)");
                }

                if (json["message"] is JValue message && message.Type == JTokenType.String && (string)message == LocalSdkMessage.ReservationNotFound)
                {
                    return new ReservationVerdict(ReservationLookupStatus.NotFound, null, true, "reservation not found: unknown, released or expired");
                }

                return new ReservationVerdict(ReservationLookupStatus.Error, null, false, "the lookup answered a 404 that is not the supervisor's 'reservation not found'");
            }

            if (status == 501)
            {
                return new ReservationVerdict(ReservationLookupStatus.Unsupported, null, false, "the local SDK endpoint has no reservation routes (its 501 fallback): a supervisor before 1.3.4");
            }

            if (status != 200)
            {
                return new ReservationVerdict(ReservationLookupStatus.Error, null, false, "the lookup answered HTTP " + status);
            }

            LocalReservation record = LocalSdkValues.TryDeserialize<LocalReservation>(body, out string problem);
            if (record == null)
            {
                return new ReservationVerdict(ReservationLookupStatus.Error, null, false, "unparseable reservation body (" + problem + ")");
            }

            if (!string.Equals(record.ReservationId, requestedId, StringComparison.Ordinal))
            {
                return new ReservationVerdict(ReservationLookupStatus.Error, null, false, "the record names a different reservationId");
            }

            if (record.ExpiresAt <= now.ToUnixTimeMilliseconds())
            {
                return new ReservationVerdict(ReservationLookupStatus.Expired, record, false, "the record's expiresAt has passed");
            }

            return new ReservationVerdict(ReservationLookupStatus.Found, record, false, null);
        }
    }
}
