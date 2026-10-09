using System;
using PingCore.Core;

namespace PingCore.Discovery.Host
{
    /// <summary>
    /// Whether this process runs under the PingCore supervisor: the supervisor names the local SDK
    /// endpoint's port in <c>AGONES_SDK_HTTP_PORT</c>. A hosted game server reaches Discovery through
    /// the supervisor's agent connection and reads its holds from the local SDK endpoint, so it
    /// never heartbeats or verifies. The same parse as the local SDK shim's (Core's
    /// <see cref="LocalSdkPort"/>), so for any value exactly one of the two tiers is active. This is the only
    /// environment variable the Host assembly reads.
    /// </summary>
    internal static class LocalSdkEndpointCheck
    {
        /// <summary>The variable the supervisor sets.</summary>
        public const string PortVariable = LocalSdkPort.Variable;

        /// <summary>True when <paramref name="readVariable"/> returns a port for <see cref="PortVariable"/>.</summary>
        public static bool IsPresent(Func<string, string> readVariable)
        {
            string raw = readVariable == null ? null : readVariable(PortVariable);
            return NamesPort(raw);
        }

        /// <summary>True when <paramref name="raw"/> names a port (<see cref="LocalSdkPort.TryParse"/>, the parse the local SDK shim uses too).</summary>
        public static bool NamesPort(string raw) => LocalSdkPort.TryParse(raw, out _);
    }
}
