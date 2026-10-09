using System;

namespace BeaconRush.Client.Models
{
    /// <summary>The Discovery apps the Beacon Rush client talks to. One Discovery client per app, because player tokens are per app.</summary>
    public enum ClientApp
    {
        /// <summary>The private app linked to the Beacon Rush fleet: matchmaking, quick join and the Fleet tab of the browser.</summary>
        Fleet,

        /// <summary>The open-registration app: the Community tab, listen hosts and self-hosted game servers.</summary>
        Community,

        /// <summary>The private self-host app (no reachability check), for self-hosted game servers on a PC behind NAT.</summary>
        SelfHost,
    }

    /// <summary>Names and the placeholder rule for <see cref="ClientApp"/>.</summary>
    public static class ClientApps
    {
        /// <summary>The id the settings ship until provisioning creates the app: <c>dscp_</c> and 32 zeros.</summary>
        public const string PlaceholderPublicId = "dscp_00000000000000000000000000000000";

        /// <summary>The app's short name (<c>fleet</c>, <c>community</c>, <c>selfhost</c>), for logs and tools.</summary>
        public static string ToName(ClientApp app)
        {
            switch (app)
            {
                case ClientApp.Fleet:
                    return "fleet";
                case ClientApp.Community:
                    return "community";
                case ClientApp.SelfHost:
                    return "selfhost";
                default:
                    throw new ArgumentOutOfRangeException(nameof(app));
            }
        }

        /// <summary>Parses <c>fleet</c>, <c>community</c> or <c>selfhost</c>, ignoring case.</summary>
        public static bool TryParse(string name, out ClientApp app)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "fleet":
                    app = ClientApp.Fleet;
                    return true;
                case "community":
                    app = ClientApp.Community;
                    return true;
                case "selfhost":
                    app = ClientApp.SelfHost;
                    return true;
                default:
                    app = ClientApp.Fleet;
                    return false;
            }
        }

        /// <summary>True when <paramref name="publicId"/> is empty or still the shipped placeholder.</summary>
        public static bool IsUnconfigured(string publicId)
        {
            return string.IsNullOrEmpty(publicId) || string.Equals(publicId, PlaceholderPublicId, StringComparison.Ordinal);
        }
    }
}
