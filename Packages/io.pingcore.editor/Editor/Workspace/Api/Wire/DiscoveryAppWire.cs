using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary><c>GET /discovery/apps</c>: the workspace's Discovery apps (the confirmation writer finds the app by <c>publicId</c>).</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/discovery/apps", WireDirection.Response, 200)]
    public sealed class DiscoveryAppListResponse
    {
        [JsonProperty("apps", NullValueHandling = NullValueHandling.Ignore)]
        public List<DiscoveryAppListItem> Apps { get; set; }

        [JsonProperty("discoveryBaseUrl", NullValueHandling = NullValueHandling.Ignore)]
        public string DiscoveryBaseUrl { get; set; }
    }

    /// <summary>
    /// <c>GET /discovery/apps/{id}</c>: the app, its fleets, its tokens (masked to the prefix and
    /// last four characters, never a digest) and its signing keys (public material only).
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/discovery/apps/{id}", WireDirection.Response, 200)]
    public sealed class DiscoveryAppDetailResponse
    {
        [JsonProperty("app", NullValueHandling = NullValueHandling.Ignore)]
        public DiscoveryAppView App { get; set; }

        [JsonProperty("linkedFleets", NullValueHandling = NullValueHandling.Ignore)]
        public List<LinkedFleetView> LinkedFleets { get; set; }

        [JsonProperty("tokens", NullValueHandling = NullValueHandling.Ignore)]
        public List<DiscoveryTokenView> Tokens { get; set; }

        [JsonProperty("signingKeys", NullValueHandling = NullValueHandling.Ignore)]
        public List<DiscoverySigningKeyView> SigningKeys { get; set; }

        [JsonProperty("discoveryBaseUrl", NullValueHandling = NullValueHandling.Ignore)]
        public string DiscoveryBaseUrl { get; set; }
    }

    /// <summary>A Discovery app row.</summary>
    [Preserve]
    public class DiscoveryAppView
    {
        [JsonProperty("discoveryAppId", NullValueHandling = NullValueHandling.Ignore)]
        public long DiscoveryAppId { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        /// <summary>The app's <c>dscp_</c> public id.</summary>
        [JsonProperty("publicId", NullValueHandling = NullValueHandling.Ignore)]
        public string PublicId { get; set; }

        [JsonProperty("gameId", NullValueHandling = NullValueHandling.Include)]
        public long? GameId { get; set; }

        [JsonProperty("gameName", NullValueHandling = NullValueHandling.Include)]
        public string GameName { get; set; }

        [JsonProperty("enabled", NullValueHandling = NullValueHandling.Ignore)]
        public bool Enabled { get; set; }

        [JsonProperty("verificationMode", NullValueHandling = NullValueHandling.Ignore)]
        public string VerificationMode { get; set; }

        /// <summary><c>private</c> or <c>open</c>.</summary>
        [JsonProperty("registrationMode", NullValueHandling = NullValueHandling.Ignore)]
        public string RegistrationMode { get; set; }

        [JsonProperty("openModeMaxServersPerIp", NullValueHandling = NullValueHandling.Include)]
        public int? OpenModeMaxServersPerIp { get; set; }

        [JsonProperty("maxServers", NullValueHandling = NullValueHandling.Ignore)]
        public int MaxServers { get; set; }

        [JsonProperty("maxSelfHostedServers", NullValueHandling = NullValueHandling.Ignore)]
        public int MaxSelfHostedServers { get; set; }

        [JsonProperty("playerRequestBudgetPerMinute", NullValueHandling = NullValueHandling.Include)]
        public int? PlayerRequestBudgetPerMinute { get; set; }

        [JsonProperty("backendRequestBudgetPerMinute", NullValueHandling = NullValueHandling.Include)]
        public int? BackendRequestBudgetPerMinute { get; set; }

        [JsonProperty("playerAuth", NullValueHandling = NullValueHandling.Ignore)]
        public PlayerAuthView PlayerAuth { get; set; }

        [JsonProperty("activeTokenCount", NullValueHandling = NullValueHandling.Include)]
        public int? ActiveTokenCount { get; set; }

        [JsonProperty("createdAt", NullValueHandling = NullValueHandling.Ignore)]
        public string CreatedAt { get; set; }

        [JsonProperty("updatedAt", NullValueHandling = NullValueHandling.Ignore)]
        public string UpdatedAt { get; set; }
    }

    /// <summary>An app in the list, which adds the fleets linked to it.</summary>
    [Preserve]
    public sealed class DiscoveryAppListItem : DiscoveryAppView
    {
        [JsonProperty("linkedFleets", NullValueHandling = NullValueHandling.Ignore, Order = 1)]
        public List<LinkedFleetView> LinkedFleets { get; set; }
    }

    /// <summary>The app's player access switches.</summary>
    [Preserve]
    public sealed class PlayerAuthView
    {
        [JsonProperty("signedEnabled", NullValueHandling = NullValueHandling.Ignore)]
        public bool SignedEnabled { get; set; }

        [JsonProperty("anonymousEnabled", NullValueHandling = NullValueHandling.Ignore)]
        public bool AnonymousEnabled { get; set; }
    }

    /// <summary>A fleet linked to a Discovery app.</summary>
    [Preserve]
    public sealed class LinkedFleetView
    {
        [JsonProperty("fleetId", NullValueHandling = NullValueHandling.Ignore)]
        public long FleetId { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }
    }

    /// <summary>A heartbeat token row: masked, never the token.</summary>
    [Preserve]
    public sealed class DiscoveryTokenView
    {
        [JsonProperty("tokenId", NullValueHandling = NullValueHandling.Ignore)]
        public long TokenId { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        /// <summary><c>dsc_...</c> plus the token's last four characters.</summary>
        [JsonProperty("maskedToken", NullValueHandling = NullValueHandling.Ignore)]
        public string MaskedToken { get; set; }

        /// <summary><c>heartbeat</c>, <c>allocate</c> or <c>both</c>.</summary>
        [JsonProperty("scope", NullValueHandling = NullValueHandling.Ignore)]
        public string Scope { get; set; }

        [JsonProperty("isActive", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsActive { get; set; }

        [JsonProperty("createdByName", NullValueHandling = NullValueHandling.Include)]
        public string CreatedByName { get; set; }

        [JsonProperty("createdAt", NullValueHandling = NullValueHandling.Ignore)]
        public string CreatedAt { get; set; }
    }

    /// <summary>A player-token signing key: public material only.</summary>
    [Preserve]
    public sealed class DiscoverySigningKeyView
    {
        [JsonProperty("keyId", NullValueHandling = NullValueHandling.Ignore)]
        public long KeyId { get; set; }

        [JsonProperty("kid", NullValueHandling = NullValueHandling.Ignore)]
        public string Kid { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        [JsonProperty("alg", NullValueHandling = NullValueHandling.Ignore)]
        public string Alg { get; set; }

        [JsonProperty("publicKeyPem", NullValueHandling = NullValueHandling.Ignore)]
        public string PublicKeyPem { get; set; }

        [JsonProperty("createdAt", NullValueHandling = NullValueHandling.Ignore)]
        public string CreatedAt { get; set; }

        [JsonProperty("createdByName", NullValueHandling = NullValueHandling.Include)]
        public string CreatedByName { get; set; }
    }
}
