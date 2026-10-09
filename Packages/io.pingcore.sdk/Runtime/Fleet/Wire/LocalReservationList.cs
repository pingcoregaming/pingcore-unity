using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>Every live reservation on this hosted game server, from the local SDK endpoint.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "GET", "/pingcore/reservations", WireDirection.Response, 200)]
    public sealed class LocalReservationList
    {
        /// <summary><c>reservations</c>.</summary>
        [JsonProperty("reservations", Required = Required.Always)]
        public List<LocalReservation> Reservations { get; set; }
    }
}
