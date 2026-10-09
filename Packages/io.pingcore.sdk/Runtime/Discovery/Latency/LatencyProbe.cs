using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// Measures the round trip to each location's WebSocket beacon, the Discovery latency procedure
    /// (pingcore.io/docs), matching the reference answers: per location with a <c>pingUrl</c>, connect, send one
    /// warm-up <c>ping</c> and discard it, then time <see cref="LatencyProbeOptions.Samples"/>
    /// sequential pings, each answered by a <c>pong</c> within <see cref="LatencyProbeOptions.SampleTimeout"/>.
    /// The median is rounded to whole ms; a sub-millisecond median is reported as 1
    /// (<see cref="LocationLatency.Clamped"/>); any failure omits the location, never 0.
    /// <para>Timing: an <c>await</c> on the Unity main thread resumes up to a frame late, which at
    /// 30 fps made every median a multiple of 33 ms. So the main thread never reads the receive
    /// time: the transport stamps each message with the <see cref="Stopwatch"/> on the thread that
    /// completed the socket read (<see cref="EchoMessage.ReceivedTimestamp"/>), the probe starts that
    /// receive before it sends, so the pong never waits in a buffer for the main thread to ask,
    /// and the send time is read immediately before the send is issued. Timeouts race
    /// <see cref="IScheduler.DelayAsync"/> with <see cref="Task.WhenAny(Task[])"/>; no timer and no
    /// <c>ConfigureAwait</c>.</para>
    /// </summary>
    public sealed class LatencyProbe
    {
        private const string Ping = "ping";
        private const string Pong = "pong";
        private const int MaxFailureText = 200;

        private readonly IScheduler scheduler;

        /// <summary>Creates a probe whose timeouts run on <paramref name="scheduler"/>.</summary>
        public LatencyProbe(IScheduler scheduler)
        {
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>
        /// True on every platform the SDK builds for. WebGL players, which have no <c>ClientWebSocket</c>, measure through the
        /// browser's WebSocket (<c>Latency/WebGL/PingCoreLatency.jslib</c>, <see cref="CreateDefaultTransport"/>). When it is
        /// false, <see cref="MeasureAsync"/> answers Unsupported and tickets omit latency.
        /// </summary>
        public static bool IsSupported => true;

        /// <summary>
        /// The beacon transport used when <see cref="LatencyProbeOptions.Transport"/> is null: the browser's WebSocket in a
        /// WebGL player (<c>WebGlEchoTransport</c>), <see cref="ClientWebSocketEchoTransport"/> everywhere else, the editor included.
        /// </summary>
        internal static IWebSocketEchoTransport CreateDefaultTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGlEchoTransport();
#else
            return new ClientWebSocketEchoTransport();
#endif
        }

        /// <summary>Measures every location that has a beacon. Never throws for a network failure.</summary>
        public async Task<LatencyResult> MeasureAsync(IReadOnlyList<Location> locations, LatencyProbeOptions options, CancellationToken cancellationToken)
        {
            if (!IsSupported)
            {
                return new LatencyResult(DiscoveryOutcome.Unsupported, null, null, "the latency probe needs ClientWebSocket, which this platform lacks");
            }

            options = options ?? new LatencyProbeOptions();
            if (options.Samples < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Samples must be at least 1");
            }

            IWebSocketEchoTransport transport = options.Transport ?? CreateDefaultTransport();
            var targets = new List<Location>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Location location in locations ?? Array.Empty<Location>())
            {
                if (location != null && !string.IsNullOrEmpty(location.PingUrl) && LatencyKeys.IsValidLocationId(location.Id) && seen.Add(location.Id))
                {
                    targets.Add(location);
                }
            }

            var results = new LocationLatency[targets.Count];
            int next = -1;
            int workers = Math.Max(1, Math.Min(options.MaxConcurrency, targets.Count));
            var running = new List<Task>(workers);
            for (int w = 0; w < workers && targets.Count > 0; w++)
            {
                running.Add(Worker());
            }

            await Task.WhenAll(running);
            if (cancellationToken.IsCancellationRequested)
            {
                return new LatencyResult(DiscoveryOutcome.Cancelled, null, Array.FindAll(results, r => r != null), "cancelled");
            }

            Dictionary<string, int> medians = LatencyMath.Map(results);
            DiscoveryOutcome outcome = targets.Count == 0 || medians.Count > 0 ? DiscoveryOutcome.Ok : DiscoveryOutcome.Unreachable;
            return new LatencyResult(outcome, medians, results, outcome == DiscoveryOutcome.Ok ? null : "every beacon failed");

            async Task Worker()
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= targets.Count)
                    {
                        return;
                    }

                    results[index] = await MeasureOneAsync(transport, targets[index], options, cancellationToken);
                }
            }
        }

        private async Task<LocationLatency> MeasureOneAsync(IWebSocketEchoTransport transport, Location location, LatencyProbeOptions options, CancellationToken cancellationToken)
        {
            IWebSocketEchoSession session = null;
            try
            {
                using (CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    session = await WithTimeout(transport.ConnectAsync(location.PingUrl, connect.Token), options.ConnectTimeout, connect, "connect");
                }

                await SampleAsync(session, options.SampleTimeout, cancellationToken);
                var samples = new List<double>(options.Samples);
                for (int i = 0; i < options.Samples; i++)
                {
                    samples.Add(await SampleAsync(session, options.SampleTimeout, cancellationToken));
                }

                if (!LatencyMath.TryMedian(samples, options.Samples, out int median, out bool clamped))
                {
                    return new LocationLatency(location.Id, null, false, "incomplete samples");
                }

                return new LocationLatency(location.Id, median, clamped, null);
            }
            catch (Exception e)
            {
                return new LocationLatency(location.Id, null, false, Describe(e));
            }
            finally
            {
                session?.Dispose();
            }
        }

        /// <summary>One timed ping: milliseconds from just before the send to the completion of the read that delivered the pong.</summary>
        private async Task<double> SampleAsync(IWebSocketEchoSession session, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (var sample = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                return await WithTimeout(PingAsync(session, sample.Token), timeout, sample, "sample");
            }
        }

        private static async Task<double> PingAsync(IWebSocketEchoSession session, CancellationToken cancellationToken)
        {
            // The receive is in flight before the ping leaves, so the pong's read completes on the
            // socket's thread and is stamped there, whenever the main thread next runs.
            Task<EchoMessage> receive = session.ReceiveTextAsync(cancellationToken);
            long start = Stopwatch.GetTimestamp();
            try
            {
                await session.SendTextAsync(Ping, cancellationToken);
            }
            catch (Exception)
            {
                Observe(receive);
                throw;
            }

            while (true)
            {
                EchoMessage message = await receive;
                if (message.Text == Pong)
                {
                    return (message.ReceivedTimestamp - start) * 1000.0 / Stopwatch.Frequency;
                }

                // Anything else is ignored, as the documented client does. A beacon sends only
                // pongs; after anything else the next read starts on the main thread, so a pong
                // already buffered by then would be stamped late.
                receive = session.ReceiveTextAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Awaits <paramref name="work"/>; when the scheduler's delay wins instead, cancels
        /// <paramref name="workScope"/> (the token the work was started with) and throws a timeout.
        /// The delay has its own scope, so work that succeeded is never cancelled afterwards.
        /// </summary>
        private async Task<T> WithTimeout<T>(Task<T> work, TimeSpan timeout, CancellationTokenSource workScope, string what)
        {
            using (var delayScope = new CancellationTokenSource())
            {
                Task delay = scheduler.DelayAsync(timeout, delayScope.Token);
                Task first = await Task.WhenAny(work, delay);
                delayScope.Cancel();
                Observe(delay);
                if (first != work)
                {
                    Observe(work);
                    workScope.Cancel();
                    if (work is Task<IWebSocketEchoSession> connecting)
                    {
                        DisposeWhenDone(connecting);
                    }

                    throw new TimeoutException(what + " timed out after " + timeout.TotalMilliseconds + " ms");
                }

                return await work;
            }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void DisposeWhenDone(Task<IWebSocketEchoSession> connecting)
        {
            connecting.ContinueWith(
                t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        t.GetAwaiter().GetResult()?.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static string Describe(Exception e)
        {
            string text = e is TimeoutException ? e.Message : e.GetType().Name + (string.IsNullOrEmpty(e.Message) ? string.Empty : ": " + e.Message);
            return text.Length > MaxFailureText ? text.Substring(0, MaxFailureText) : text;
        }
    }
}
