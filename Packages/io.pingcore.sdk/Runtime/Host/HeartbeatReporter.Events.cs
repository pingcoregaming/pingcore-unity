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
    /// <summary>What the reporter tells the game: the accepted status, the one-time warnings, the Beat event and the log.</summary>
    public sealed partial class HeartbeatReporter
    {
        private HeartbeatStatus AcceptedStatus(HeartbeatResponse response)
        {
            return new HeartbeatStatus(running, stopped, response.VerificationMode, response.Verified, response.LastProbeError, response.ExpiresIn, Now, 0);
        }

        private DateTimeOffset Now => scheduler.UtcNow;

        private void RaiseBeat(HeartbeatResult result)
        {
            Action<HeartbeatResult> handler = Beat;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(result);
            }
            catch (Exception e)
            {
                Write(HeartbeatLogLevel.Warning, "heartbeat", "a Beat handler threw " + e.GetType().Name, 0);
            }
        }

        private void Write(HeartbeatLogLevel level, string call, string message, int status)
        {
            try
            {
                log(new HeartbeatLogEntry(level, call, message, status));
            }
            catch (Exception)
            {
                // A throwing log sink must never stop the heartbeat.
            }
        }

        private static void WriteToConsole(HeartbeatLogEntry entry)
        {
            if (entry.Level == HeartbeatLogLevel.Warning)
            {
                Debug.LogWarning(LogPrefix + entry);
            }
            else
            {
                Debug.Log(LogPrefix + entry);
            }
        }

        /// <summary>
        /// Warns once when Discovery verifies this app with <c>tcp</c> while a <c>queryPort</c> is sent: the
        /// probe then dials that port over TCP, which the UDP echo responder cannot answer, so the game server
        /// stays unverified. Set <see cref="HeartbeatReporterOptions.OmitQueryPort"/> to have it dial the game
        /// port, or point <see cref="HeartbeatReporterOptions.QueryPort"/> at a TCP listener.
        /// </summary>
        private void WarnIfTcpProbesTheQueryPort(HeartbeatResponse response)
        {
            if (response == null || !queryPort.HasValue || !string.Equals(response.VerificationMode, "tcp", StringComparison.Ordinal))
            {
                return;
            }

            lock (gate)
            {
                if (tcpQueryPortWarned)
                {
                    return;
                }

                tcpQueryPortWarned = true;
            }

            Write(HeartbeatLogLevel.Warning, "heartbeat", "this app verifies by tcp, which dials queryPort " + queryPort.Value
                + "; a UDP echo responder cannot answer it, so the listing stays unverified. Set OmitQueryPort to have Discovery dial the game port, or point QueryPort at a TCP listener", 0);
        }
    }
}
