using Newtonsoft.Json;

namespace PingCore.Core.Wire
{
    /// <summary>
    /// A Discovery error body: <c>{error: true, message}</c>, plus a machine-readable
    /// <c>reason</c> and, for some reasons, the numbers that explain it (<c>available</c> for
    /// <c>no_seats</c>, <c>limit</c> and <c>active</c> for the per-app ceilings, <c>limit</c>
    /// for the heartbeat caps). Absent fields stay absent on re-serialization.
    /// One contract per error status the SDK branches on, for every Discovery route the Client and
    /// Host assemblies call (the shared 401, 403, 429 and 503 included); each has its schema in the
    /// snapshot, and the fixtures named <c>error.*.json</c> cover them.
    /// </summary>
    [Preserve]
    // Player-token issuance (Client).
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/player-tokens", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/player-tokens", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/player-tokens", WireDirection.Error, 503)]
    // Public reads (Client).
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/servers", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/servers", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/locations", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/locations", WireDirection.Error, 503)]
    // Tickets (Client, player token).
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Error, 400)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Error, 409)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 404)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 404)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 409)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Error, 503)]
    // Reservations and quick join (Client, player token).
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Error, 400)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Error, 404)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Error, 409)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/servers/{serverId}/reservations", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Error, 400)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Error, 409)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 404)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 403)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/reservations/{reservationId}", WireDirection.Error, 503)]
    // Heartbeat tier (Host, heartbeat token).
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/heartbeat", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/heartbeat", WireDirection.Error, 409)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/heartbeat", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/heartbeat", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/servers/{serverId}", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/servers/{serverId}", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/servers/{serverId}", WireDirection.Error, 503)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/reservations/verify/{reservationId}", WireDirection.Error, 401)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/reservations/verify/{reservationId}", WireDirection.Error, 429)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/reservations/verify/{reservationId}", WireDirection.Error, 503)]
    public sealed class ErrorEnvelope : WireResponse
    {
        /// <summary><c>message</c>: human-readable; never branch on it.</summary>
        [JsonProperty("message", Required = Required.Always)]
        public string Message { get; set; }

        /// <summary><c>reason</c>: the machine-readable code, when the operation documents one.</summary>
        [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)]
        public string Reason { get; set; }

        /// <summary><c>limit</c>: the ceiling that was hit (<c>too_many_*</c>, <c>self_hosted_cap</c>, <c>ip_cap</c>).</summary>
        [JsonProperty("limit", NullValueHandling = NullValueHandling.Ignore)]
        public int? Limit { get; set; }

        /// <summary><c>active</c>: how many are already active (<c>too_many_*</c>).</summary>
        [JsonProperty("active", NullValueHandling = NullValueHandling.Ignore)]
        public int? Active { get; set; }

        /// <summary><c>available</c>: free seats left on the game server (<c>no_seats</c>).</summary>
        [JsonProperty("available", NullValueHandling = NullValueHandling.Ignore)]
        public int? Available { get; set; }

        /// <summary><see cref="Reason"/> parsed; <see cref="DiscoveryReason.Unknown"/> when absent or new.</summary>
        [JsonIgnore]
        public DiscoveryReason ReasonCode => DiscoveryReasons.Parse(Reason);
    }
}
