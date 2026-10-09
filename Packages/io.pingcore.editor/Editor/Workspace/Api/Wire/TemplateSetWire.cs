using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary><c>GET /my-games/{gameId}/template-sets/{templateSetId}</c>: the set and its configs. The Push check reads the process name of its active command-line config (the game's startup command); the answer is an explicit, credential-free column list.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/my-games/{gameId}/template-sets/{templateSetId}", WireDirection.Response, 200)]
    public sealed class TemplateSetResponse
    {
        [JsonProperty("templateSetId", NullValueHandling = NullValueHandling.Ignore)]
        public long TemplateSetId { get; set; }

        [JsonProperty("gameId", NullValueHandling = NullValueHandling.Ignore)]
        public long GameId { get; set; }

        [JsonProperty("gameName", NullValueHandling = NullValueHandling.Include)]
        public string GameName { get; set; }

        [JsonProperty("setName", NullValueHandling = NullValueHandling.Include)]
        public string SetName { get; set; }

        [JsonProperty("setDescription", NullValueHandling = NullValueHandling.Include)]
        public string SetDescription { get; set; }

        [JsonProperty("configs", NullValueHandling = NullValueHandling.Ignore)]
        public List<TemplateConfigView> Configs { get; set; }
    }

    /// <summary>A config of a template set.</summary>
    [Preserve]
    public sealed class TemplateConfigView
    {
        [JsonProperty("templateId", NullValueHandling = NullValueHandling.Ignore)]
        public long TemplateId { get; set; }

        [JsonProperty("filename", NullValueHandling = NullValueHandling.Include)]
        public string Filename { get; set; }

        [JsonProperty("directory", NullValueHandling = NullValueHandling.Include)]
        public string Directory { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Include)]
        public string Description { get; set; }

        /// <summary><c>cli</c>, <c>file</c> or <c>script</c>.</summary>
        [JsonProperty("templateType", NullValueHandling = NullValueHandling.Include)]
        public string TemplateType { get; set; }

        [JsonProperty("scriptHook", NullValueHandling = NullValueHandling.Include)]
        public string ScriptHook { get; set; }

        [JsonProperty("scriptInterpreter", NullValueHandling = NullValueHandling.Include)]
        public string ScriptInterpreter { get; set; }

        [JsonProperty("scriptTimeoutSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int ScriptTimeoutSeconds { get; set; }

        [JsonProperty("active", NullValueHandling = NullValueHandling.Ignore)]
        public bool Active { get; set; }

        [JsonProperty("webAccess", NullValueHandling = NullValueHandling.Ignore)]
        public bool WebAccess { get; set; }

        [JsonProperty("alwaysWrite", NullValueHandling = NullValueHandling.Ignore)]
        public bool AlwaysWrite { get; set; }

        [JsonProperty("processName", NullValueHandling = NullValueHandling.Include)]
        public string ProcessName { get; set; }
    }
}
