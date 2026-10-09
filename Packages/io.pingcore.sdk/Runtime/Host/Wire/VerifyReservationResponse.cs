using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Host.Wire
{
    /// <summary>
    /// The answer to a reservation verify. An open app's shipped heartbeat token gets the
    /// verdict only (<c>{error, valid}</c>); any other token gets the detailed answer, or
    /// <c>valid: false</c> with a <c>reason</c> (<c>wrong_server</c>, <c>not_in_reservation</c>).
    /// Every field after <c>valid</c> is therefore optional, and the nullable ones carry a
    /// <c>...Specified</c> flag so a field sent as null is told apart from a field not sent.
    /// The hosted path never calls verify.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/reservations/verify/{reservationId}", WireDirection.Response, 200)]
    public sealed class VerifyReservationResponse : WireResponse
    {
        private List<string> playerIds;
        private JObject context;
        private string ownerPlayerId;

        /// <summary><c>valid</c>: the verdict. Admit only on true.</summary>
        [JsonProperty("valid", Required = Required.Always)]
        public bool Valid { get; set; }

        /// <summary><c>reason</c>: present only with <c>valid: false</c> on a detailed answer.</summary>
        [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)]
        public string Reason { get; set; }

        /// <summary><c>reservationId</c>.</summary>
        [JsonProperty("reservationId", NullValueHandling = NullValueHandling.Ignore)]
        public string ReservationId { get; set; }

        /// <summary><c>serverId</c>: equals the <c>serverId</c> sent.</summary>
        [JsonProperty("serverId", NullValueHandling = NullValueHandling.Ignore)]
        public string ServerId { get; set; }

        /// <summary><c>seats</c>.</summary>
        [JsonProperty("seats", NullValueHandling = NullValueHandling.Ignore)]
        public int? Seats { get; set; }

        /// <summary><c>playerIds</c>: the named players, or null for open seats.</summary>
        [JsonProperty("playerIds", NullValueHandling = NullValueHandling.Include)]
        public List<string> PlayerIds { get => playerIds; set { playerIds = value; PlayerIdsSpecified = true; } }

        /// <summary>True when <c>playerIds</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool PlayerIdsSpecified { get; set; }

        /// <summary><c>context</c>: the hold's context, only with a matching <c>serverId</c>; object or null.</summary>
        [JsonProperty("context", NullValueHandling = NullValueHandling.Include)]
        public JObject Context { get => context; set { context = value; ContextSpecified = true; } }

        /// <summary>True when <c>context</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool ContextSpecified { get; set; }

        /// <summary><c>expiresAt</c>: hold expiry, epoch milliseconds.</summary>
        [JsonProperty("expiresAt", NullValueHandling = NullValueHandling.Ignore)]
        public long? ExpiresAt { get; set; }

        /// <summary><c>ownerKind</c>: <c>backend</c> or <c>player</c>.</summary>
        [JsonProperty("ownerKind", NullValueHandling = NullValueHandling.Ignore)]
        public string OwnerKind { get; set; }

        /// <summary><c>ownerPlayerId</c>: the owning player's id, or null for a backend hold.</summary>
        [JsonProperty("ownerPlayerId", NullValueHandling = NullValueHandling.Include)]
        public string OwnerPlayerId { get => ownerPlayerId; set { ownerPlayerId = value; OwnerPlayerIdSpecified = true; } }

        /// <summary>True when <c>ownerPlayerId</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool OwnerPlayerIdSpecified { get; set; }

        /// <summary><see cref="Reason"/> parsed; <see cref="DiscoveryReason.Unknown"/> when absent.</summary>
        [JsonIgnore]
        public DiscoveryReason ReasonCode => DiscoveryReasons.Parse(Reason);
    }
}
