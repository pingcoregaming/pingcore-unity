using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Host.Wire
{
    /// <summary>The answer to a heartbeat, with the verification verdict Discovery holds.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/heartbeat", WireDirection.Response, 200)]
    public sealed class HeartbeatResponse : WireResponse
    {
        private string verified;
        private string lastProbeError;

        /// <summary><c>serverId</c>: the id Discovery recorded.</summary>
        [JsonProperty("serverId", Required = Required.Always)]
        public string ServerId { get; set; }

        /// <summary><c>ip</c>: the address Discovery recorded.</summary>
        [JsonProperty("ip", Required = Required.Always)]
        public string Ip { get; set; }

        /// <summary><c>expiresIn</c>: seconds until the entry expires without another heartbeat.</summary>
        [JsonProperty("expiresIn", Required = Required.Always)]
        public int ExpiresIn { get; set; }

        /// <summary><c>verificationMode</c>: <c>none</c>, <c>tcp</c> or <c>udp-echo</c>.</summary>
        [JsonProperty("verificationMode", Required = Required.Always)]
        public string VerificationMode { get; set; }

        /// <summary><c>verified</c>: <c>pending</c>, <c>verified</c>, <c>unverified</c>, or null when the mode is <c>none</c>.</summary>
        [JsonProperty("verified", NullValueHandling = NullValueHandling.Include)]
        public string Verified { get => verified; set { verified = value; VerifiedSpecified = true; } }

        /// <summary>True when <c>verified</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool VerifiedSpecified { get; set; }

        /// <summary><c>lastProbeError</c>: why the last probe failed, or null.</summary>
        [JsonProperty("lastProbeError", NullValueHandling = NullValueHandling.Include)]
        public string LastProbeError { get => lastProbeError; set { lastProbeError = value; LastProbeErrorSpecified = true; } }

        /// <summary>True when <c>lastProbeError</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool LastProbeErrorSpecified { get; set; }
    }
}
