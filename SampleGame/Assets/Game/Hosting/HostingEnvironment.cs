using System;
using BeaconRush.Networking;
using Newtonsoft.Json.Linq;

namespace BeaconRush.Hosting
{
    /// <summary>The studio-defined fields a listed Beacon Rush game server sends with its heartbeat: <c>{"proto": 2}</c>, so a browser can filter on <c>meta.proto</c>.</summary>
    public static class HeartbeatMeta
    {
        public const string ProtocolKey = "proto";

        public static JObject Create() => new JObject { [ProtocolKey] = BeaconRushProtocol.Version };
    }

    /// <summary>
    /// The one place Beacon Rush reads a credential: the heartbeat token of the self-hosted dedicated mode and the
    /// online listen host, from the environment variable <see cref="DiscoveryTokenVariable"/> only. Never from a
    /// command-line argument (other users on the machine can read a process's arguments), an asset or a file. The
    /// value is handed straight to <c>HeartbeatReporterOptions.Token</c> and never logged or put in an event.
    /// </summary>
    public static class HostingEnvironment
    {
        public const string DiscoveryTokenVariable = "PINGCORE_DISCOVERY_TOKEN";

        /// <summary>The Discovery service the heartbeat tier talks to when the caller names none.</summary>
        public const string DefaultDiscoveryUrl = "https://discovery.pingcore.io";

        /// <summary>The heartbeat token, or null when the variable is unset or blank.</summary>
        public static string DiscoveryToken()
        {
            string value = Environment.GetEnvironmentVariable(DiscoveryTokenVariable);
            return HostingModeSelector.HasToken(value) ? value.Trim() : null;
        }
    }
}
