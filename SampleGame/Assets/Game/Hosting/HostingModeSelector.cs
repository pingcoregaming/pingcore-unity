using PingCore.Core.Handshake;

namespace BeaconRush.Hosting
{
    /// <summary>How this Beacon Rush process hosts. The event literal is <see cref="HostingModeSelector.Wire"/>.</summary>
    public enum GameHostingMode
    {
        /// <summary>PingCore-hosted: the local SDK endpoint answered. Allocations, the <c>players</c> counter, holds from the supervisor.</summary>
        Hosted,

        /// <summary>Self-hosted dedicated: no local SDK endpoint, a heartbeat token in <c>PINGCORE_DISCOVERY_TOKEN</c>.</summary>
        SelfHosted,

        /// <summary>A dedicated game server with neither: listens, talks to nobody, takes <c>lan</c> tickets only (a local smoke test).</summary>
        Local,

        /// <summary>A player build that hosts (<see cref="ListenHost"/>): online with a heartbeat, or LAN only.</summary>
        Listen,
    }

    /// <summary>The selector's answer: a mode and why, or a failure (the local SDK endpoint is configured but did not answer).</summary>
    public readonly struct HostingSelection
    {
        public const string ReasonLocalSdkEndpoint = "local_sdk_endpoint";
        public const string ReasonDiscoveryToken = "discovery_token";
        public const string ReasonNoEndpointNoToken = "no_endpoint_no_token";
        public const string ReasonListenOnline = "listen_online";
        public const string ReasonListenLan = "listen_lan";
        public const string ReasonEndpointUnreachable = "local_sdk_endpoint_unreachable";

        public HostingSelection(GameHostingMode mode, string reason, bool lanOnly, bool failed)
        {
            Mode = mode;
            Reason = reason;
            LanOnly = lanOnly;
            Failed = failed;
        }

        public GameHostingMode Mode { get; }

        /// <summary>The <c>hosting</c> event's <c>reason</c>.</summary>
        public string Reason { get; }

        /// <summary>No Discovery at all: <c>lan</c> tickets only.</summary>
        public bool LanOnly { get; }

        /// <summary>The game server must not run: the endpoint is configured but never answered. It never falls back to another mode.</summary>
        public bool Failed { get; }

        /// <summary>The SDK's hosting mode for the approval: the unlisted local mode admits as a LAN-only listen host does.</summary>
        public HostingMode ApprovalMode => Mode == GameHostingMode.Hosted ? HostingMode.Hosted
            : Mode == GameHostingMode.SelfHosted ? HostingMode.SelfHosted
            : HostingMode.Listen;
    }

    /// <summary>
    /// Picks the hosting mode of a dedicated server build once, at startup, from what it finds around it and never
    /// from anything a player sends. Pure:
    /// <list type="table">
    /// <item><term>The local SDK endpoint is configured (<c>AGONES_SDK_HTTP_PORT</c>) and answered <c>StartAsync</c></term><description>Hosted, whatever the token</description></item>
    /// <item><term>Configured but no answer</term><description>Failed: quit with 1 (a hosted game server never heartbeats)</description></item>
    /// <item><term>Not configured, <c>PINGCORE_DISCOVERY_TOKEN</c> set (not blank)</term><description>Self-hosted dedicated</description></item>
    /// <item><term>Neither</term><description>Unlisted: listens, admits <c>lan</c> tickets only</description></item>
    /// </list>
    /// A listen host is chosen by the player instead (<see cref="ForListen"/>).
    /// </summary>
    public static class HostingModeSelector
    {
        public static HostingSelection Select(bool localSdkEndpointConfigured, bool localSdkEndpointAnswered, string discoveryToken)
        {
            if (localSdkEndpointConfigured)
            {
                return localSdkEndpointAnswered
                    ? new HostingSelection(GameHostingMode.Hosted, HostingSelection.ReasonLocalSdkEndpoint, false, false)
                    : new HostingSelection(GameHostingMode.Hosted, HostingSelection.ReasonEndpointUnreachable, false, true);
            }

            return HasToken(discoveryToken)
                ? new HostingSelection(GameHostingMode.SelfHosted, HostingSelection.ReasonDiscoveryToken, false, false)
                : new HostingSelection(GameHostingMode.Local, HostingSelection.ReasonNoEndpointNoToken, true, false);
        }

        /// <summary>A listen host: online with a heartbeat, or LAN only.</summary>
        public static HostingSelection ForListen(bool lanOnly) =>
            new HostingSelection(GameHostingMode.Listen, lanOnly ? HostingSelection.ReasonListenLan : HostingSelection.ReasonListenOnline, lanOnly, false);

        /// <summary>True when the token variable holds something other than whitespace. The value itself is never looked at further here.</summary>
        public static bool HasToken(string token) => !string.IsNullOrWhiteSpace(token);

        /// <summary>The <c>mode</c> literal: <c>hosted</c>, <c>selfHosted</c>, <c>local</c>, <c>listen</c>.</summary>
        public static string Wire(GameHostingMode mode)
        {
            switch (mode)
            {
                case GameHostingMode.Hosted:
                    return "hosted";
                case GameHostingMode.SelfHosted:
                    return "selfHosted";
                case GameHostingMode.Local:
                    return "local";
                case GameHostingMode.Listen:
                    return "listen";
                default:
                    return "unknown";
            }
        }
    }
}
