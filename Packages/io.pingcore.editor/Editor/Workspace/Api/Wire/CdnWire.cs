using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary>
    /// <c>POST /cdn-sources/sources/{id}/push-token</c>: the new token, shown once. Issuing kills
    /// the previous token at once. Internal: the client moves <see cref="Token"/> straight into
    /// the credential store and clears it; no caller ever sees this type.
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "POST", "/cdn-sources/sources/{id}/push-token", WireDirection.Response, 200)]
    public sealed class PushTokenIssueResponse
    {
        [JsonProperty("token", NullValueHandling = NullValueHandling.Ignore)]
        public string Token { get; set; }

        [JsonProperty("created", NullValueHandling = NullValueHandling.Include)]
        public string Created { get; set; }

        /// <summary>Never prints the token.</summary>
        public override string ToString() => "PushTokenIssueResponse(token withheld)";
    }

    /// <summary>
    /// <c>GET /cdn-sources/push/info</c>, sent with a push token (never the brand member's key), modelled key-only: the
    /// source the token belongs to. "Use an existing push token" sends a pasted token here before keeping it, as
    /// <c>pingctl</c> does before every push. The answer carries fields the plugin has no use for, some of them
    /// sensitive; the client keeps <see cref="Source"/>'s id and name and nothing else
    /// (<see cref="PingCoreApiClient.CheckPushTokenAsync"/>), so the rest is never modelled, kept, logged or shown.
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/cdn-sources/push/info", WireDirection.Response, 200)]
    public sealed class PushInfoResponse
    {
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public PushInfoSourceView Source { get; set; }
    }

    /// <summary>The source a push token denotes (the answer's <c>source</c>, key-only).</summary>
    [Preserve]
    public sealed class PushInfoSourceView
    {
        [JsonProperty("sourceId", NullValueHandling = NullValueHandling.Ignore)]
        public long SourceId { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Include)]
        public string Name { get; set; }
    }
}
