using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>The answer to releasing a reservation.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Response, 200)]
    public sealed class ReleaseReservationResponse : WireResponse
    {
        /// <summary><c>reservationId</c>.</summary>
        [JsonProperty("reservationId", Required = Required.Always)]
        public string ReservationId { get; set; }

        /// <summary><c>released</c>: always true.</summary>
        [JsonProperty("released", Required = Required.Always)]
        public bool Released { get; set; }
    }
}
