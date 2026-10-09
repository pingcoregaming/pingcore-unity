using System.Globalization;

namespace BeaconRush.Client.Models
{
    /// <summary>How a listen host is announced.</summary>
    public enum HostReach
    {
        /// <summary>Heartbeats the community app with the token from <c>PINGCORE_DISCOVERY_TOKEN</c>; admits reservation joins (verified).</summary>
        Online,

        /// <summary>Talks to no Discovery; admits <c>lan</c> join tickets only (Direct connect).</summary>
        LanOnly,
    }

    /// <summary>Where the Host game screen is.</summary>
    public enum HostStatus
    {
        /// <summary>Not hosting.</summary>
        Idle,

        /// <summary>Starting the host and, Online, the first heartbeat.</summary>
        Starting,

        /// <summary>Hosting.</summary>
        Hosting,

        /// <summary>Could not start, or Discovery refused the heartbeat.</summary>
        Failed,
    }

    /// <summary>
    /// The Host game screen, pure: Online or LAN only, the game port and the echo port above it, whether
    /// Online is possible (the heartbeat token comes only from the environment), and the status line.
    /// </summary>
    public sealed class HostModel
    {
        /// <summary>The default game port.</summary>
        public const int DefaultPort = 7777;

        /// <summary>The lowest port a host may use (below are privileged or well known).</summary>
        public const int MinPort = 1024;

        /// <summary>The highest game port (its echo port is one above).</summary>
        public const int MaxPort = 65534;

        /// <summary>Creates the model; <paramref name="tokenAvailable"/> is whether <c>PINGCORE_DISCOVERY_TOKEN</c> is set (never its value).</summary>
        public HostModel(bool tokenAvailable)
        {
            TokenAvailable = tokenAvailable;
            Reach = tokenAvailable ? HostReach.Online : HostReach.LanOnly;
        }

        /// <summary>True when the environment holds a heartbeat token.</summary>
        public bool TokenAvailable { get; }

        /// <summary>Online or LAN only.</summary>
        public HostReach Reach { get; private set; }

        /// <summary>The port text as typed.</summary>
        public string PortText { get; set; } = DefaultPort.ToString(CultureInfo.InvariantCulture);

        /// <summary>Where the screen is.</summary>
        public HostStatus Status { get; private set; } = HostStatus.Idle;

        /// <summary>The failure or heartbeat note shown under the status.</summary>
        public string Detail { get; private set; }

        /// <summary>The game port the host is using, once started.</summary>
        public int ActivePort { get; private set; }

        /// <summary>Chooses Online or LAN only; Online needs a token.</summary>
        public bool SetReach(HostReach reach)
        {
            if (reach == HostReach.Online && !TokenAvailable)
            {
                return false;
            }

            Reach = reach;
            return true;
        }

        /// <summary>The game port typed, when it is a valid one.</summary>
        public bool TryGetPort(out int port, out string problem)
        {
            problem = null;
            if (!int.TryParse((PortText ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < MinPort || port > MaxPort)
            {
                problem = "the port must be a number from " + MinPort + " to " + MaxPort;
                port = 0;
                return false;
            }

            return true;
        }

        /// <summary>The ports a player forwards on the router for an Online host. Pure.</summary>
        public static string PortsToForward(int gamePort)
        {
            return "UDP " + gamePort + " (game) and UDP " + (gamePort + 1) + " (Discovery's reachability echo)";
        }

        /// <summary>The host is starting on a port.</summary>
        public void Starting(int port)
        {
            Status = HostStatus.Starting;
            ActivePort = port;
            Detail = null;
        }

        /// <summary>The host is up; <paramref name="detail"/> is the heartbeat note (Online) or null.</summary>
        public void Hosting(string detail)
        {
            Status = HostStatus.Hosting;
            Detail = detail;
        }

        /// <summary>The host could not start or was refused.</summary>
        public void Fail(string detail)
        {
            Status = HostStatus.Failed;
            Detail = string.IsNullOrEmpty(detail) ? "the host could not start" : detail;
        }

        /// <summary>The host stopped.</summary>
        public void Stopped()
        {
            Status = HostStatus.Idle;
            Detail = null;
            ActivePort = 0;
        }

        /// <summary>The status line.</summary>
        public string StatusText()
        {
            switch (Status)
            {
                case HostStatus.Idle:
                    return Reach == HostReach.Online
                        ? "Online: listed on the community tab. Forward " + PortsToForward(TryGetPort(out int p, out _) ? p : DefaultPort) + "."
                        : "LAN only: players on your network use Direct connect with your address and port.";
                case HostStatus.Starting:
                    return "Starting on port " + ActivePort + "...";
                case HostStatus.Hosting:
                    return (Reach == HostReach.Online ? "Hosting online on " : "Hosting on your LAN on ") + "port " + ActivePort
                        + (Reach == HostReach.Online ? "; forward " + PortsToForward(ActivePort) : string.Empty) + "."
                        + (string.IsNullOrEmpty(Detail) ? string.Empty : " " + Detail);
                default:
                    return "Hosting failed: " + Detail;
            }
        }

        /// <summary>The note under the Online button when no token is set.</summary>
        public static string NoTokenHint => "Online hosting needs a heartbeat token in the environment variable PINGCORE_DISCOVERY_TOKEN when the game starts.";
    }
}
