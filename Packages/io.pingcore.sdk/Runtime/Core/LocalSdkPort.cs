using System.Globalization;

namespace PingCore.Core
{
    /// <summary>
    /// The one parse of <c>AGONES_SDK_HTTP_PORT</c>, the variable the PingCore supervisor sets to name the
    /// local SDK endpoint's port: 1 to 5 ASCII digits whose value is 1 to 65535, nothing else (no sign, no
    /// space, no other digit script). The local SDK shim is active, and the heartbeat tier refuses to start,
    /// exactly when this accepts the value, so for any value exactly one tier is active. Pure.
    /// </summary>
    public static class LocalSdkPort
    {
        /// <summary>The variable the supervisor sets.</summary>
        public const string Variable = "AGONES_SDK_HTTP_PORT";

        /// <summary>True when <paramref name="raw"/> names a port; <paramref name="port"/> is then 1 to 65535, else 0.</summary>
        public static bool TryParse(string raw, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(raw) || raw.Length > 5)
            {
                return false;
            }

            foreach (char c in raw)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            int value = int.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
            if (value < 1 || value > 65535)
            {
                return false;
            }

            port = value;
            return true;
        }
    }
}
