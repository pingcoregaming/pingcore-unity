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
    /// <summary>Stopping: the delist on a clean stop, disposal without one, and waking or cancelling the loop.</summary>
    public sealed partial class HeartbeatReporter
    {
        /// <summary>
        /// Stops the loop and delists: <c>DELETE /v1/servers/{serverId}</c> with the serverId encoded
        /// (a default <c>ip:port</c> id carries a colon). The sample calls it from <c>Application.quitting</c>.
        /// Without an accepted heartbeat there is nothing to delist and the result is a local refusal.
        /// </summary>
        public async Task<DiscoveryResult<DelistResponse>> StopAsync(CancellationToken cancellationToken)
        {
            Task loop;
            string id;
            DiscoveryCaller discovery;
            lock (gate)
            {
                stopped = true;
                running = false;
                loop = loopTask;
                id = serverId;
                discovery = caller;
                status = new HeartbeatStatus(false, true, status.VerificationMode, status.Verified, status.LastProbeError, status.ExpiresIn, status.LastAcceptedAt, status.MissedBeats);
            }

            CancelLoop();
            await loop;
            if (discovery == null || id == null)
            {
                return DiscoveryResult<DelistResponse>.Refused(DiscoveryReason.Unknown, "nothing to delist: no heartbeat was accepted");
            }

            DiscoveryRequest request = DiscoveryRequest.Create("DELETE", "/v1/servers/" + DiscoveryCaller.Segment(id), "DELETE /v1/servers/{serverId}").WithBearer(token);
            DiscoveryResult<DelistResponse> result = await discovery.SendAsync<DelistResponse>(request, cancellationToken);
            if (result.IsOk)
            {
                Write(HeartbeatLogLevel.Info, "delist", id + (result.Value.Removed ? " delisted" : " was already gone"), result.Status);
            }
            else
            {
                Write(HeartbeatLogLevel.Warning, "delist", "failed: " + result + "; the entry lapses " + (int)HeartbeatSchedule.Ttl.TotalSeconds + " s after the last accepted heartbeat", result.Status);
            }

            return result;
        }

        /// <summary>Stops the loop without delisting (the entry lapses with the TTL). Use <see cref="StopAsync"/> for a clean stop.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                stopped = true;
                running = false;
                status = new HeartbeatStatus(false, true, status.VerificationMode, status.Verified, status.LastProbeError, status.ExpiresIn, status.LastAcceptedAt, status.MissedBeats);
            }

            CancelLoop();
        }

        private static void Wake(CancellationTokenSource wake)
        {
            if (wake == null)
            {
                return;
            }

            try
            {
                wake.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The wait it belonged to has already ended.
            }
        }

        private void CancelLoop()
        {
            CancellationTokenSource source;
            lock (gate)
            {
                source = loopSource;
                loopSource = null;
            }

            if (source != null)
            {
                source.Cancel();
            }
        }
    }
}
