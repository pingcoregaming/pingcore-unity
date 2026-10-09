using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>
    /// A reservation read back by id. Not a <see cref="ReservationResponse"/>: it carries
    /// <c>playerIds</c> and has no <c>ip</c>, <c>port</c>, <c>replayed</c> or <c>expiresIn</c>.
    /// A released or expired hold, or one this caller may not see, is a 404 instead.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Response, 200)]
    public sealed class ReservationRecord : WireResponse
    {
        private List<string> playerIds;

        /// <summary><c>reservationId</c>.</summary>
        [JsonProperty("reservationId", Required = Required.Always)]
        public string ReservationId { get; set; }

        /// <summary><c>serverId</c>.</summary>
        [JsonProperty("serverId", Required = Required.Always)]
        public string ServerId { get; set; }

        /// <summary><c>status</c>: <c>pending</c> or <c>released</c>.</summary>
        [JsonProperty("status", Required = Required.Always)]
        public string Status { get; set; }

        /// <summary><c>seats</c>.</summary>
        [JsonProperty("seats", Required = Required.Always)]
        public int Seats { get; set; }

        /// <summary><c>playerIds</c>: the named seats, or null when the hold names none.</summary>
        [JsonProperty("playerIds", NullValueHandling = NullValueHandling.Include)]
        public List<string> PlayerIds { get => playerIds; set { playerIds = value; PlayerIdsSpecified = true; } }

        /// <summary>True when <c>playerIds</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool PlayerIdsSpecified { get; set; }

        /// <summary><c>createdAt</c>: epoch milliseconds.</summary>
        [JsonProperty("createdAt", Required = Required.Always)]
        public long CreatedAt { get; set; }

        /// <summary><c>expiresAt</c>: hold expiry, epoch milliseconds.</summary>
        [JsonProperty("expiresAt", Required = Required.Always)]
        public long ExpiresAt { get; set; }

        /// <summary><c>ownerKind</c>: <c>backend</c> or <c>player</c>.</summary>
        [JsonProperty("ownerKind", Required = Required.Always)]
        public string OwnerKind { get; set; }

        /// <summary><c>ownerPlayerId</c>: the owning player's id, or null for a backend hold.</summary>
        [JsonProperty("ownerPlayerId", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public string OwnerPlayerId { get; set; }
    }
}
