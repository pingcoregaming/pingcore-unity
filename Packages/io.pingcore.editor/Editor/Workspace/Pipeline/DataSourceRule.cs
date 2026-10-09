namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// The plugin pushes to CDN sources only: a fleet whose game files come from a
    /// container image or from Steam is pushed and released outside the plugin, and this rule says so in the
    /// words Ship (a branch's image sentence) and Release and the planner (the fleet's deployments) show. Pure.
    /// </summary>
    public static class DataSourceRule
    {
        /// <summary>The data source the plugin pushes to.</summary>
        public const string Cdn = "cdn_source";

        /// <summary>A container image the studio pushes itself.</summary>
        public const string Image = "image";

        /// <summary>Steam depots.</summary>
        public const string Steam = "steam_depot";

        /// <summary>The sentence for an image fleet (the spec's own words).</summary>
        public const string ImageMessage = "This fleet runs a container image. The plugin pushes to CDN sources only; push your image and release it outside the plugin.";

        /// <summary>The sentence for a Steam fleet.</summary>
        public const string SteamMessage = "This fleet's game files come from Steam. The plugin pushes to CDN sources only; release Steam builds in the panel or with MCP.";

        /// <summary>Null for a CDN source; otherwise why the plugin cannot push or release this fleet.</summary>
        public static string Refusal(string dataSourceType)
        {
            switch (dataSourceType)
            {
                case Cdn:
                    return null;
                case Image:
                    return ImageMessage;
                case Steam:
                    return SteamMessage;
                default:
                    return "This fleet's game files do not come from a CDN source PingCore can version, so the plugin cannot push or release it. Check the game branch's data source in the panel.";
            }
        }
    }
}
