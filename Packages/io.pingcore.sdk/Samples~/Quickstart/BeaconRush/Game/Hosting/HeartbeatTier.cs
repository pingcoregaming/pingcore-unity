using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host;
using PingCore.Discovery.Host.Wire;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// When a failed first heartbeat is tried again, pure: 5, 10 and 20 s after the first, second and third failure,
    /// then every 30 s. A refusal (409 <c>ip_cap</c> or <c>self_hosted_cap</c>) is final and never retried.
    /// </summary>
    public static class HeartbeatStartRetry
    {
        public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

        /// <summary>The wait after <paramref name="failures"/> failed starts in a row (1 or more).</summary>
        public static TimeSpan DelayAfter(int failures)
        {
            switch (failures)
            {
                case 1:
                    return TimeSpan.FromSeconds(5);
                case 2:
                    return TimeSpan.FromSeconds(10);
                case 3:
                    return TimeSpan.FromSeconds(20);
                default:
                    return failures < 1 ? TimeSpan.Zero : MaxDelay;
            }
        }

        /// <summary>True when a start that ended with <paramref name="outcome"/> is tried again.</summary>
        public static bool Retries(HeartbeatStartOutcome outcome) => outcome == HeartbeatStartOutcome.Failed;
    }

    /// <summary>
    /// The heartbeat tier of a self-hosted dedicated game server or an online listen host: the
    /// <see cref="HeartbeatReporter"/> that lists it on a Discovery app (its token comes from
    /// <see cref="HostingEnvironment.DiscoveryToken"/>, never logged), the <see cref="UdpEchoResponder"/> that answers
    /// Discovery's DSCV1 reachability probe on game port + 1, the start retries (<see cref="HeartbeatStartRetry"/>),
    /// and the delist on stop. Raises <c>heartbeat</c>, <c>echo</c> and <c>delist</c> events. Main thread only.
    /// </summary>
    internal sealed class HeartbeatTier : IDisposable
    {
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly int gamePort;
        private UdpEchoResponder echo;
        private int lastEchoed = -1;
        private int lastDropped = -1;
        private int lastThrottled = -1;
        private bool stopped;

        public HeartbeatTier(HeartbeatReporterOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            gamePort = options.GamePort;
            QueryPort = options.EffectiveQueryPort;
            options.Log = OnLog;
            Reporter = HeartbeatReporter.Create(options);
            Reporter.Beat += OnBeat;
        }

        public HeartbeatReporter Reporter { get; }

        /// <summary>The outcome of the latest start attempt, or null before the first.</summary>
        public HeartbeatStartOutcome? LastStartOutcome { get; private set; }

        /// <summary>The UDP port of the echo, which the heartbeat sends as <c>queryPort</c>.</summary>
        public int QueryPort { get; }

        /// <summary>
        /// Binds the echo and sends the first heartbeat. A failed start is retried in the background on the
        /// <see cref="HeartbeatStartRetry"/> schedule until it starts, is refused, or the tier stops; the first
        /// attempt's outcome is returned.
        /// </summary>
        public async Task<HeartbeatStartOutcome> StartAsync(CancellationToken cancellationToken)
        {
            BindEcho();
            HeartbeatStartOutcome first = await AttemptAsync(1, cancellationToken);
            if (HeartbeatStartRetry.Retries(first) && !stopped)
            {
                _ = RetryAsync();
            }

            return first;
        }

        /// <summary>Stops the loop and delists (the answer may not arrive before the process exits), then closes the echo. Idempotent.</summary>
        public async Task StopAsync()
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            lifetime.Cancel();
            string serverId = Reporter.ServerId;
            try
            {
                DiscoveryResult<DelistResponse> delist = await Reporter.StopAsync(CancellationToken.None);
                if (serverId != null)
                {
                    ServerEvents.Raise(ServerEvents.Delist, "serverId", serverId, "status", delist.Status,
                        "outcome", EventWire.Camel(delist.Outcome), "removed", delist.IsOk && delist.Value != null ? (object)delist.Value.Removed : null);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                RaiseEchoStats(true);
                echo?.Dispose();
                echo = null;
            }
        }

        public void Dispose()
        {
            stopped = true;
            lifetime.Cancel();
            Reporter.Beat -= OnBeat;
            Reporter.Dispose();
            echo?.Dispose();
            echo = null;
        }

        private void BindEcho()
        {
            if (UdpEchoResponder.TryBind(QueryPort, out UdpEchoResponder responder, out string error))
            {
                echo = responder;
                ServerEvents.Raise(ServerEvents.Echo, "phase", "bound", "port", QueryPort, "echoed", 0, "dropped", 0, "throttled", 0, "error", null);
                return;
            }

            ServerEvents.Raise(ServerEvents.Echo, "phase", "failed", "port", QueryPort, "echoed", 0, "dropped", 0, "throttled", 0, "error", error);
            Debug.LogWarning("[BeaconRush] the reachability echo could not bind UDP " + QueryPort + " (game port " + gamePort + " + 1): " + error);
        }

        private async Task<HeartbeatStartOutcome> AttemptAsync(int attempt, CancellationToken cancellationToken)
        {
            HeartbeatStartResult start = await Reporter.StartAsync(cancellationToken);
            LastStartOutcome = start.Outcome;
            HeartbeatStatus status = Reporter.Status;
            ServerEvents.Raise(ServerEvents.Heartbeat,
                "phase", "start",
                "accepted", start.IsStarted,
                "status", start.Call == null ? 0 : start.Call.Status,
                "outcome", start.Call == null ? null : EventWire.Camel(start.Call.Outcome),
                "startOutcome", EventWire.Camel(start.Outcome),
                "attempt", attempt,
                "serverId", Reporter.ServerId,
                "verificationMode", status.VerificationMode,
                "verified", status.Verified,
                "lastProbeError", status.LastProbeError,
                "reason", start.Outcome == HeartbeatStartOutcome.Refused && start.Call != null ? start.Call.ReasonWire : null,
                "limit", start.Limit,
                "missedBeats", status.MissedBeats,
                "stopped", status.Stopped);
            return start.Outcome;
        }

        private async Task RetryAsync()
        {
            CancellationToken token = lifetime.Token;
            for (int failures = 1; !token.IsCancellationRequested; failures++)
            {
                try
                {
                    await Awaitable.WaitForSecondsAsync((float)HeartbeatStartRetry.DelayAfter(failures).TotalSeconds, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (stopped || token.IsCancellationRequested)
                {
                    return;
                }

                HeartbeatStartOutcome outcome = await AttemptAsync(failures + 1, token);
                if (!HeartbeatStartRetry.Retries(outcome))
                {
                    return;
                }
            }
        }

        private void OnBeat(HeartbeatResult beat)
        {
            HeartbeatStatus status = Reporter.Status;
            DiscoveryCallResult call = beat.Call;
            ServerEvents.Raise(ServerEvents.Heartbeat,
                "phase", "beat",
                "accepted", beat.Accepted,
                "status", call == null ? 0 : call.Status,
                "outcome", call == null ? null : EventWire.Camel(call.Outcome),
                "startOutcome", null,
                "attempt", null,
                "serverId", Reporter.ServerId,
                "verificationMode", status.VerificationMode,
                "verified", status.Verified,
                "lastProbeError", status.LastProbeError,
                "reason", call != null && !call.IsOk ? call.ReasonWire : null,
                "limit", call != null && call.Error != null ? call.Error.Limit : null,
                "missedBeats", beat.MissedBeats,
                "stopped", beat.Stopped);
            RaiseEchoStats(false);
        }

        private void RaiseEchoStats(bool always)
        {
            UdpEchoResponder responder = echo;
            if (responder == null)
            {
                return;
            }

            int echoed = responder.Echoed;
            int dropped = responder.Dropped;
            int throttled = responder.Throttled;
            if (!always && echoed == lastEchoed && dropped == lastDropped && throttled == lastThrottled)
            {
                return;
            }

            lastEchoed = echoed;
            lastDropped = dropped;
            lastThrottled = throttled;
            ServerEvents.Raise(ServerEvents.Echo, "phase", "stats", "port", responder.Port, "echoed", echoed, "dropped", dropped, "throttled", throttled, "error", null);
        }

        private static void OnLog(HeartbeatLogEntry entry)
        {
            // The reporter's entries never carry the token.
            string line = "[PingCore.Host] " + entry;
            switch (entry.Level)
            {
                case HeartbeatLogLevel.Warning:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }
        }
    }
}
