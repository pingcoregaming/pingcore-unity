using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>A seat hold, from reserving on a chosen game server or from quick join (spec schema <c>ReservationResponse</c>).</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Response, 200)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Response, 200)]
    public sealed class ReservationResponse : WireResponse
    {
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

        /// <summary><c>ip</c>: string or null.</summary>
        [JsonProperty("ip", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public string Ip { get; set; }

        /// <summary><c>port</c>: integer or null.</summary>
        [JsonProperty("port", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public int? Port { get; set; }

        /// <summary><c>createdAt</c>: epoch milliseconds.</summary>
        [JsonProperty("createdAt", Required = Required.Always)]
        public long CreatedAt { get; set; }

        /// <summary><c>expiresAt</c>: hold expiry, epoch milliseconds.</summary>
        [JsonProperty("expiresAt", Required = Required.Always)]
        public long ExpiresAt { get; set; }

        /// <summary><c>expiresIn</c>: seconds left on the hold.</summary>
        [JsonProperty("expiresIn", Required = Required.Always)]
        public int ExpiresIn { get; set; }

        /// <summary><c>replayed</c>: true when an idempotency anchor replayed the original outcome.</summary>
        [JsonProperty("replayed", Required = Required.Always)]
        public bool Replayed { get; set; }

        /// <summary><c>ownerKind</c>: <c>backend</c> or <c>player</c>.</summary>
        [JsonProperty("ownerKind", Required = Required.Always)]
        public string OwnerKind { get; set; }

        /// <summary><c>ownerPlayerId</c>: the owning player's id, or null for a backend hold.</summary>
        [JsonProperty("ownerPlayerId", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public string OwnerPlayerId { get; set; }
    }
}
