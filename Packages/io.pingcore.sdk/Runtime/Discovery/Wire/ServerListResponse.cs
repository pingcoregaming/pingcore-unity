using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>One page of the public server list.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/servers", WireDirection.Response, 200)]
    public sealed class ServerListResponse : WireResponse
    {
        /// <summary><c>servers</c>: the page.</summary>
        [JsonProperty("servers", Required = Required.Always)]
        public List<PublicServer> Servers { get; set; }

        /// <summary><c>totalServers</c>: matches after filtering, before paging.</summary>
        [JsonProperty("totalServers", Required = Required.Always)]
        public int TotalServers { get; set; }

        /// <summary><c>returned</c>: entries on this page.</summary>
        [JsonProperty("returned", Required = Required.Always)]
        public int Returned { get; set; }

        /// <summary><c>limit</c>: page size in force.</summary>
        [JsonProperty("limit", Required = Required.Always)]
        public int Limit { get; set; }

        /// <summary><c>offset</c>: page offset in force.</summary>
        [JsonProperty("offset", Required = Required.Always)]
        public int Offset { get; set; }
    }
}
