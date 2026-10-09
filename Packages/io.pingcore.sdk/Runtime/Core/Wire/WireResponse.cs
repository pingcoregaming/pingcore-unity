using Newtonsoft.Json;

namespace PingCore.Core.Wire
{
    /// <summary>
    /// The Discovery envelope flag. Every Discovery body carries <c>error</c> at the top level,
    /// beside the operation's own fields (<c>false</c> on success, <c>true</c> on an error).
    /// Local SDK endpoint bodies have no envelope and do not derive from this type.
    /// </summary>
    [Preserve]
    public abstract class WireResponse
    {
        /// <summary><c>error</c>: false on success, true on an error body.</summary>
        [JsonProperty("error", Required = Required.Always)]
        public bool Error { get; set; }
    }
}
