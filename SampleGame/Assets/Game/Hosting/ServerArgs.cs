using System;
using System.Collections.Generic;
using System.Globalization;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The dedicated server's command line: <c>-port &lt;n&gt;</c> (the template renders
    /// <c>-port %GAMEPORT|USERVAL%</c>), 1 to 65535, default <see cref="DefaultPort"/>. Flags match
    /// case-insensitively; the last <c>-port</c> wins. A malformed port is an error, never a silent
    /// fallback, so a misrendered template is visible at once. The game server identity never comes
    /// from here or from the environment: it is the local SDK endpoint's <c>GET /gameserver</c>.
    /// </summary>
    public sealed class ServerArgs
    {
        public const ushort DefaultPort = 7777;
        public const string PortFlag = "-port";
        public const string PortSourceArgument = "argument";
        public const string PortSourceDefault = "default";

        private ServerArgs(ushort port, string portSource, string error)
        {
            Port = port;
            PortSource = portSource;
            Error = error;
        }

        public ushort Port { get; }

        /// <summary><see cref="PortSourceArgument"/> or <see cref="PortSourceDefault"/>.</summary>
        public string PortSource { get; }

        /// <summary>Why the command line is unusable, or null.</summary>
        public string Error { get; }

        public bool IsValid => Error == null;

        /// <summary>Parses the arguments; element 0 may be the executable path, which is never a flag.</summary>
        public static ServerArgs Parse(IReadOnlyList<string> args)
        {
            ushort port = DefaultPort;
            string source = PortSourceDefault;
            if (args == null)
            {
                return new ServerArgs(port, source, null);
            }

            for (int i = 0; i < args.Count; i++)
            {
                if (!string.Equals(args[i], PortFlag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= args.Count)
                {
                    return new ServerArgs(DefaultPort, PortSourceDefault, PortFlag + " needs a value");
                }

                string value = args[++i];
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1 || parsed > ushort.MaxValue)
                {
                    return new ServerArgs(DefaultPort, PortSourceDefault, PortFlag + " must be a whole number from 1 to 65535");
                }

                port = (ushort)parsed;
                source = PortSourceArgument;
            }

            return new ServerArgs(port, source, null);
        }
    }
}
