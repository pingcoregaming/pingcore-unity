using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>An issued anonymous player token. The token is a bearer secret: never log it.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/player-tokens", WireDirection.Response, 200)]
    public sealed class PlayerTokenResponse : WireResponse
    {
        /// <summary><c>token</c>: the compact JWT.</summary>
        [JsonProperty("token", Required = Required.Always)]
        public string Token { get; set; }

        /// <summary><c>tokenType</c>: <c>anonymous</c>.</summary>
        [JsonProperty("tokenType", Required = Required.Always)]
        public string TokenType { get; set; }

        /// <summary><c>playerId</c>: the token <c>sub</c>, <c>anon:</c> followed by a UUID.</summary>
        [JsonProperty("playerId", Required = Required.Always)]
        public string PlayerId { get; set; }

        /// <summary><c>expiresAt</c>: epoch milliseconds.</summary>
        [JsonProperty("expiresAt", Required = Required.Always)]
        public long ExpiresAt { get; set; }

        /// <summary><c>expiresIn</c>: seconds until expiry.</summary>
        [JsonProperty("expiresIn", Required = Required.Always)]
        public int ExpiresIn { get; set; }

        /// <summary>Never includes the token.</summary>
        public override string ToString() => $"PlayerTokenResponse(playerId={PlayerId}, expiresIn={ExpiresIn})";
    }
}
