using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Discovery.Host
{
    /// <summary>The heartbeat loop: build, send, decide (<c>HeartbeatSchedule</c>), wait.</summary>
    public sealed partial class HeartbeatReporter
    {
        private async Task RunLoopAsync(TimeSpan firstDelay, CancellationToken stop)
        {
            TimeSpan delay = firstDelay;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!await WaitUntilDueAsync(delay, stop))
                    {
                        return;
                    }

                    DiscoveryResult<HeartbeatResponse> result = await SendBeatAsync(stop);
                    int before;
                    lock (gate)
                    {
                        before = failures;
                    }

                    HeartbeatStep step = HeartbeatSchedule.Next(result, before, randomUnit());
                    if (step.Kind == HeartbeatStepKind.Cancelled || stop.IsCancellationRequested)
                    {
                        return;
                    }

                    Apply(result, step);
                    if (step.Kind == HeartbeatStepKind.Stop)
                    {
                        return;
                    }

                    delay = step.Delay;
                }
            }
            catch (Exception e)
            {
                // Never expected (no call throws for HTTP or transport failures); keep it visible.
                lock (gate)
                {
                    running = false;
                    status = new HeartbeatStatus(false, status.Stopped, status.VerificationMode, status.Verified, status.LastProbeError, status.ExpiresIn, status.LastAcceptedAt, status.MissedBeats);
                }

                Write(HeartbeatLogLevel.Warning, "heartbeat", "the heartbeat loop ended on " + e.GetType().Name, 0);
            }
        }

        /// <summary>Records one loop send: failures, status, the missed-beats warning, the Beat event.</summary>
        private void Apply(DiscoveryResult<HeartbeatResponse> result, HeartbeatStep step)
        {
            bool recovered = false;
            bool warn = false;
            bool refused = step.Kind == HeartbeatStepKind.Stop;
            lock (gate)
            {
                failures = step.Failures;
                if (result.IsOk)
                {
                    recovered = missedWarned;
                    missedWarned = false;
                    serverId = result.Value.ServerId ?? serverId;
                    status = AcceptedStatus(result.Value);
                }
                else
                {
                    if (refused)
                    {
                        running = false;
                        stopped = true;
                    }

                    if (failures >= HeartbeatSchedule.MissedBeatsWarning && !missedWarned)
                    {
                        missedWarned = true;
                        warn = true;
                    }

                    status = new HeartbeatStatus(running, stopped, status.VerificationMode, status.Verified, status.LastProbeError, status.ExpiresIn, status.LastAcceptedAt, failures);
                }
            }

            if (recovered)
            {
                Write(HeartbeatLogLevel.Info, "heartbeat", "accepted again after missed beats", result.Status);
            }

            if (result.IsOk)
            {
                WarnIfTcpProbesTheQueryPort(result.Value);
            }

            if (warn)
            {
                Write(HeartbeatLogLevel.Warning, "heartbeat", step.Failures + " heartbeats in a row missed (last: " + result + "); Discovery drops the listing " + (int)HeartbeatSchedule.Ttl.TotalSeconds + " s after the last accepted one", result.Status);
            }

            if (refused)
            {
                int? limit = result.Error == null ? null : result.Error.Limit;
                Write(HeartbeatLogLevel.Warning, "heartbeat", "refused: " + result.ReasonWire + " (limit " + limit + "), after the entry lapsed; nothing more is sent", result.Status);
            }

            RaiseBeat(new HeartbeatResult(result, step.Failures, refused ? (TimeSpan?)null : step.Delay, refused));
        }

        /// <summary>
        /// Waits <paramref name="delay"/>, or less for a pending change (<c>HeartbeatSchedule.DueAt</c>).
        /// A <see cref="SetPlayers"/> or <see cref="SetMeta"/> call wakes the wait so the due time is
        /// recomputed. False when the reporter is stopping.
        /// </summary>
        private async Task<bool> WaitUntilDueAsync(TimeSpan delay, CancellationToken stop)
        {
            DateTimeOffset regularDue = scheduler.UtcNow + delay;
            while (true)
            {
                DateTimeOffset due;
                var wake = new CancellationTokenSource();
                lock (gate)
                {
                    due = HeartbeatSchedule.DueAt(regularDue, changePending, failures, lastSendAt);
                    wakeSource = wake;
                }

                TimeSpan wait = due - scheduler.UtcNow;
                try
                {
                    if (wait <= TimeSpan.Zero)
                    {
                        return !stop.IsCancellationRequested;
                    }

                    using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stop, wake.Token))
                    {
                        try
                        {
                            await scheduler.DelayAsync(wait, linked.Token);
                            return !stop.IsCancellationRequested;
                        }
                        catch (OperationCanceledException)
                        {
                            if (stop.IsCancellationRequested)
                            {
                                return false;
                            }

                            // Woken by a change: recompute the due time.
                        }
                    }
                }
                finally
                {
                    lock (gate)
                    {
                        if (wakeSource == wake)
                        {
                            wakeSource = null;
                        }
                    }

                    wake.Dispose();
                }
            }
        }

        /// <summary>Builds the body from the current state and sends one heartbeat.</summary>
        private async Task<DiscoveryResult<HeartbeatResponse>> SendBeatAsync(CancellationToken cancellationToken)
        {
            HeartbeatRequest body;
            DiscoveryCaller discovery;
            lock (gate)
            {
                body = new HeartbeatRequest
                {
                    Name = name,
                    Port = gamePort,
                    Players = players,
                    MaxPlayers = maxPlayers,
                };
                if (queryPort.HasValue)
                {
                    // Left unset (not sent at all) with OmitQueryPort: Discovery then probes the game port.
                    body.QueryPort = queryPort;
                }

                string id = serverId ?? configuredServerId;
                if (id != null)
                {
                    body.ServerId = id;
                }

                if (ip != null)
                {
                    body.Ip = ip;
                }

                if (version != null)
                {
                    body.Version = version;
                }

                if (meta != null)
                {
                    body.Meta = (Newtonsoft.Json.Linq.JObject)meta.DeepClone();
                }

                changePending = false;
                lastSendAt = scheduler.UtcNow;
                discovery = caller;
            }

            DiscoveryRequest request = DiscoveryRequest.Json("POST", "/v1/heartbeat", body, "POST /v1/heartbeat").WithBearer(token);
            return await discovery.SendAsync<HeartbeatResponse>(request, cancellationToken);
        }
    }
}
