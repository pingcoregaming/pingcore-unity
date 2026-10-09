using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>The answer to cancelling a matchmaking ticket.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Response, 200)]
    public sealed class CancelTicketResponse : WireResponse
    {
        /// <summary><c>ticketId</c>.</summary>
        [JsonProperty("ticketId", Required = Required.Always)]
        public string TicketId { get; set; }

        /// <summary><c>cancelled</c>: always true.</summary>
        [JsonProperty("cancelled", Required = Required.Always)]
        public bool Cancelled { get; set; }
    }
}
