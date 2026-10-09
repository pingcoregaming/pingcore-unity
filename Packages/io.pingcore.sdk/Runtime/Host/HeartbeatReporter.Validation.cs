using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;
using PingCore.Unity;
using UnityEngine;

namespace PingCore.Discovery.Host
{
    /// <summary>The options check before the first send, and the local refusal it answers with.</summary>
    public sealed partial class HeartbeatReporter
    {
        /// <summary>Null when the options can be sent; otherwise why not. Creates the caller.</summary>
        private string Validate()
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("dsc_", StringComparison.Ordinal))
            {
                return "Token must be a Discovery app's dsc_ heartbeat token";
            }

            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                return "Name must be 1 to 100 characters";
            }

            if (gamePort < 1 || gamePort > 65535)
            {
                return "GamePort must be 1 to 65535";
            }

            if (queryPortConflict)
            {
                return "set QueryPort or OmitQueryPort, not both";
            }

            if (queryPort.HasValue && (queryPort.Value < 1 || queryPort.Value > 65535))
            {
                return "QueryPort must be 1 to 65535";
            }

            if (maxPlayers < 0 || maxPlayers > MaxPlayersCeiling)
            {
                return "MaxPlayers must be 0 to 1000000";
            }

            if (version != null && version.Trim().Length > 200)
            {
                return "Version must be at most 200 characters";
            }

            if (ip != null && (ip.Length == 0 || ip.Length > 45))
            {
                return "Ip must be 1 to 45 characters";
            }

            if (configuredServerId != null && !ServerIdPattern.IsMatch(configuredServerId))
            {
                return "ServerId must be 1 to 128 of A-Z a-z 0-9 . _ : [ ] -";
            }

            try
            {
                var created = new DiscoveryCaller(baseUrl, transport ?? new UnityWebRequestTransport(TimeSpan.FromSeconds(10)));
                lock (gate)
                {
                    caller = created;
                }
            }
            catch (ArgumentException)
            {
                return "BaseUrl must be an absolute http or https URL";
            }

            return null;
        }

        private static HeartbeatStartResult Failed(string message)
        {
            return new HeartbeatStartResult(HeartbeatStartOutcome.Failed, DiscoveryCallResult.Refused(DiscoveryReason.Unknown, message), null, null);
        }
    }
}
