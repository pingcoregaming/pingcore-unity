using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary>
    /// <c>GET /my-games/{id}</c>, modelled key-only: the game's id, its name, for each branch only what Push needs (its
    /// names, the data source, the CDN source and the platform), and each template set's id and name (the startup command
    /// check of a fleet with no deployment reads them). The branch's default deployment spec is deliberately not modelled:
    /// it is a legacy setting the plugin never relies on. The answer carries fields the plugin has no use for, some of
    /// them sensitive; the client keeps only the modelled keys and drops the rest unread
    /// (<see cref="PingCoreApiClient.GetGameBranchesAsync"/>): the rest is never modelled, kept, logged or shown.
    /// Never add a key here that the plugin does not need.
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/my-games/{id}", WireDirection.Response, 200)]
    public sealed class GameBranchesResponse
    {
        [JsonProperty("gameId", NullValueHandling = NullValueHandling.Ignore)]
        public long GameId { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Include)]
        public string Name { get; set; }

        [JsonProperty("gameBranches", NullValueHandling = NullValueHandling.Ignore)]
        public List<GameBranchView> GameBranches { get; set; }

        /// <summary>The game's template sets that are not removed, id and name only.</summary>
        [JsonProperty("templateSets", NullValueHandling = NullValueHandling.Ignore)]
        public List<GameTemplateSetView> TemplateSets { get; set; }
    }

    /// <summary>A template set of the game, key-only (the API's template set row; the rest is dropped unread).</summary>
    [Preserve]
    public sealed class GameTemplateSetView
    {
        [JsonProperty("templateSetId", NullValueHandling = NullValueHandling.Ignore)]
        public long TemplateSetId { get; set; }

        [JsonProperty("setName", NullValueHandling = NullValueHandling.Include)]
        public string SetName { get; set; }
    }

    /// <summary>A branch of a game, key-only (the API's branch row; the rest of the row is dropped unread).</summary>
    [Preserve]
    public sealed class GameBranchView
    {
        [JsonProperty("gameBranchId", NullValueHandling = NullValueHandling.Ignore)]
        public long GameBranchId { get; set; }

        /// <summary>
        /// The branch's legacy identifier: the API fills it with <c>branch-&lt;id&gt;</c> when the
        /// branch is created, so it is not what the panel calls the branch. See <see cref="BranchDescription"/>.
        /// </summary>
        [JsonProperty("branchName", NullValueHandling = NullValueHandling.Include)]
        public string BranchName { get; set; }

        /// <summary>
        /// The branch's name as the panel shows it (its page title and breadcrumbs, and the Add branch form's required
        /// field): <c>Main</c>. Despite the key, this is the name; <see cref="DisplayName"/> prefers it.
        /// </summary>
        [JsonProperty("branchDescription", NullValueHandling = NullValueHandling.Include)]
        public string BranchDescription { get; set; }

        /// <summary><c>linux</c> or <c>windows</c>.</summary>
        [JsonProperty("platform", NullValueHandling = NullValueHandling.Include)]
        public string Platform { get; set; }

        /// <summary>1 for the game's default branch.</summary>
        [JsonProperty("defaultBranch", NullValueHandling = NullValueHandling.Ignore)]
        public int DefaultBranch { get; set; }

        /// <summary><c>none</c>, <c>cdn_source</c>, <c>image</c> or <c>steam_depot</c>.</summary>
        [JsonProperty("dataSourceType", NullValueHandling = NullValueHandling.Include)]
        public string DataSourceType { get; set; }

        /// <summary>The CDN source the branch delivers from, or null.</summary>
        [JsonProperty("cdnSourceId", NullValueHandling = NullValueHandling.Include)]
        public long? CdnSourceId { get; set; }

        /// <summary>The name every plugin message uses: the panel's (<see cref="BranchDescription"/>), else <see cref="BranchName"/>, else <c>#id</c>. Pure.</summary>
        public string DisplayName()
        {
            if (!string.IsNullOrWhiteSpace(BranchDescription))
            {
                return BranchDescription.Trim();
            }

            return string.IsNullOrWhiteSpace(BranchName) ? "#" + GameBranchId : BranchName.Trim();
        }
    }
}
