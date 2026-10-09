using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// The <c>{message}</c> body the local SDK endpoint answers a refusal with: a reservation
    /// that is unknown, released or expired (<c>reservation not found</c>), an unknown counter,
    /// a malformed counter PATCH or joinable body. Only the reservation 404 has a JSON schema in
    /// the snapshot spec, so that is the one <c>[WireContract]</c>.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "GET", "/pingcore/reservations/{reservationId}", WireDirection.Error, 404)]
    public sealed class LocalSdkMessage
    {
        /// <summary>The message the supervisor sends for an unknown, released or expired reservation.</summary>
        public const string ReservationNotFound = "reservation not found";

        /// <summary><c>message</c>.</summary>
        [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)]
        public string Message { get; set; }
    }
}
