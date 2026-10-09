namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary>
    /// The contract source name for the PingCore API, used in every editor DTO's
    /// <c>[WireContract]</c>. A PingCore API contract describes the envelope's <c>data</c> object
    /// (the client strips <c>{error, message, data}</c> itself), and its path is the route template
    /// with a leading slash (<c>/fleets/{id}/releases</c>). A contract checker matches it to the API
    /// contract snapshots (not published) and, for the fleet routes, walks it against the API's
    /// own OpenAPI document.
    /// </summary>
    public static class PingCoreApiContract
    {
        /// <summary>The <c>source</c> of every PingCore API <c>[WireContract]</c>.</summary>
        public const string Source = "pingcore-api";
    }
}
