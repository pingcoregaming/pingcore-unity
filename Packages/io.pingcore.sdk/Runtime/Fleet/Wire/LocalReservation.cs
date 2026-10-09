using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// A live Discovery reservation on this hosted game server, as the local SDK endpoint serves
    /// it (spec schema <c>Reservation</c>: the Discovery push minus its frame type). This is how
    /// the hosted path admits a reserved player; it never calls verify. 404 means unknown,
    /// released or expired.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "GET", "/pingcore/reservations/{reservationId}", WireDirection.Response, 200)]
    public sealed class LocalReservation
    {
        /// <summary><c>reservationId</c>.</summary>
        [JsonProperty("reservationId", Required = Required.Always)]
        public string ReservationId { get; set; }

        /// <summary><c>serverId</c>: this game server's Discovery server id.</summary>
        [JsonProperty("serverId", Required = Required.Always)]
        public string ServerId { get; set; }

        /// <summary><c>seats</c>.</summary>
        [JsonProperty("seats", Required = Required.Always)]
        public int Seats { get; set; }

        /// <summary><c>playerIds</c>: who may use the hold, or null for open seats.</summary>
        [JsonProperty("playerIds", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public List<string> PlayerIds { get; set; }

        /// <summary><c>context</c>: caller-supplied data, verbatim; object or null.</summary>
        [JsonProperty("context", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public JObject Context { get; set; }

        /// <summary><c>expiresAt</c>: epoch milliseconds; never served after this.</summary>
        [JsonProperty("expiresAt", Required = Required.Always)]
        public long ExpiresAt { get; set; }

        /// <summary><c>ownerKind</c>: <c>backend</c> or <c>player</c>.</summary>
        [JsonProperty("ownerKind", Required = Required.Always)]
        public string OwnerKind { get; set; }

        /// <summary><c>ownerPlayerId</c>: string or null.</summary>
        [JsonProperty("ownerPlayerId", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public string OwnerPlayerId { get; set; }
    }
}
