using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>The platform's locations, identical for every Discovery app.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/locations", WireDirection.Response, 200)]
    public sealed class LocationsResponse : WireResponse
    {
        /// <summary><c>locations</c>.</summary>
        [JsonProperty("locations", Required = Required.Always)]
        public List<Location> Locations { get; set; }

        /// <summary><c>returned</c>.</summary>
        [JsonProperty("returned", Required = Required.Always)]
        public int Returned { get; set; }
    }
}
