using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>
    /// A seat hold on a chosen game server. Unset fields are not sent. The SDK always sends a
    /// <c>reservationId</c> (minted when the caller gives none) and reuses it on its own retry,
    /// so a retry after a lost answer replays the hold instead of holding twice.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Request, 0)]
    public sealed class ReserveRequest
    {
        /// <summary><c>reservationId</c>: idempotency anchor, <c>^[A-Za-z0-9_.:-]{1,100}$</c>.</summary>
        [JsonProperty("reservationId", NullValueHandling = NullValueHandling.Ignore)]
        public string ReservationId { get; set; }

        /// <summary><c>seats</c>: seats to hold; the whole party fits or nothing is held.</summary>
        [JsonProperty("seats", NullValueHandling = NullValueHandling.Ignore)]
        public int? Seats { get; set; }

        /// <summary><c>playerIds</c>: one opaque id per seat; when present, exactly <c>seats</c> entries.</summary>
        [JsonProperty("playerIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> PlayerIds { get; set; }

        /// <summary><c>ttlSeconds</c>: hold length, 5 to 300.</summary>
        [JsonProperty("ttlSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? TtlSeconds { get; set; }

        /// <summary><c>context</c>: opaque object delivered to the reserved game server.</summary>
        [JsonProperty("context", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Context { get; set; }
    }
}
