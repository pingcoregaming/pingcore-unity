using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary>
    /// <c>GET /me/capabilities</c> (<c>API\Me\Capabilities</c>, login only): who the key acts for
    /// and the brand permissions it holds. Sign-in shows the workspace's name from it, and the panel links
    /// take the workspace panel's base from <c>brandUrl</c> (<c>UI/Common/PanelLinks.cs</c>).
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/me/capabilities", WireDirection.Response, 200)]
    public sealed class CapabilitiesResponse
    {
        [JsonProperty("identity", NullValueHandling = NullValueHandling.Ignore)]
        public CapabilitiesIdentity Identity { get; set; }

        /// <summary>The brand permission keys: the brand's enabled ones for its owner, the member's own otherwise, none for a customer.</summary>
        [JsonProperty("permissions", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Permissions { get; set; }
    }

    /// <summary>The identity block of <see cref="CapabilitiesResponse"/>. The email is the caller's own; the plugin never shows or stores it.</summary>
    [Preserve]
    public sealed class CapabilitiesIdentity
    {
        [JsonProperty("userId", NullValueHandling = NullValueHandling.Ignore)]
        public long UserId { get; set; }

        [JsonProperty("email", NullValueHandling = NullValueHandling.Include)]
        public string Email { get; set; }

        /// <summary><c>workspace</c> for a brand member, <c>customer</c> otherwise.</summary>
        [JsonProperty("persona", NullValueHandling = NullValueHandling.Ignore)]
        public string Persona { get; set; }

        [JsonProperty("isBrandMember", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsBrandMember { get; set; }

        [JsonProperty("isOwner", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsOwner { get; set; }

        [JsonProperty("brandId", NullValueHandling = NullValueHandling.Include)]
        public long? BrandId { get; set; }

        /// <summary>The workspace's name, shown after sign-in.</summary>
        [JsonProperty("brandName", NullValueHandling = NullValueHandling.Include)]
        public string BrandName { get; set; }

        [JsonProperty("brandDomain", NullValueHandling = NullValueHandling.Include)]
        public string BrandDomain { get; set; }

        [JsonProperty("brandUrl", NullValueHandling = NullValueHandling.Include)]
        public string BrandUrl { get; set; }
    }
}
