using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace BeaconRush.Client.Models
{
    /// <summary>An IPv4 game server address and UDP port, as typed on Direct connect or passed as <c>ip:port</c>.</summary>
    public readonly struct GameEndpoint
    {
        /// <summary>The IPv4 loopback address, for a game server on this machine.</summary>
        public const string Loopback = "127.0.0.1";

        /// <summary>Creates an endpoint.</summary>
        public GameEndpoint(string address, ushort port)
        {
            Address = address;
            Port = port;
        }

        /// <summary>The dotted IPv4 address.</summary>
        public string Address { get; }

        /// <summary>The UDP port, 1 to 65535.</summary>
        public ushort Port { get; }

        /// <summary>The same port on 127.0.0.1: a game server on this PC behind NAT.</summary>
        public GameEndpoint ToLoopback() => new GameEndpoint(Loopback, Port);

        /// <inheritdoc />
        public override string ToString() => Address + ":" + Port.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses <c>a.b.c.d:port</c> (<c>localhost</c> means 127.0.0.1). Pure. Anything else, including a
        /// host name, an IPv6 address or port 0, is refused with a problem the screen can show.
        /// </summary>
        public static bool TryParse(string text, out GameEndpoint endpoint, out string problem)
        {
            endpoint = default;
            problem = null;
            string trimmed = (text ?? string.Empty).Trim();
            int colon = trimmed.LastIndexOf(':');
            if (colon <= 0 || colon == trimmed.Length - 1 || trimmed.IndexOf(':') != colon)
            {
                problem = "enter an address and port, for example 192.168.1.20:7777";
                return false;
            }

            string host = trimmed.Substring(0, colon);
            if (string.Equals(host, "localhost", System.StringComparison.OrdinalIgnoreCase))
            {
                host = Loopback;
            }

            if (!IsDottedIPv4(host))
            {
                problem = "the address must be an IPv4 address such as 192.168.1.20";
                return false;
            }

            if (!ushort.TryParse(trimmed.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) || port == 0)
            {
                problem = "the port must be a number from 1 to 65535";
                return false;
            }

            endpoint = new GameEndpoint(host, port);
            return true;
        }

        /// <summary>True for exactly four dot-separated decimal octets. Pure.</summary>
        public static bool IsDottedIPv4(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string[] parts = text.Split('.');
            if (parts.Length != 4)
            {
                return false;
            }

            foreach (string part in parts)
            {
                if (part.Length == 0 || part.Length > 3 || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value > 255)
                {
                    return false;
                }
            }

            return IPAddress.TryParse(text, out IPAddress parsed) && parsed.AddressFamily == AddressFamily.InterNetwork;
        }
    }
}
