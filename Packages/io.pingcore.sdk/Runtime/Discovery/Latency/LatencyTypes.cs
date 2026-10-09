using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core.Discovery;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The WebSocket seam of the latency probe: one connection per location beacon. The default is
    /// <see cref="ClientWebSocketEchoTransport"/> (<c>System.Net.WebSockets.ClientWebSocket</c>), or the browser's
    /// WebSocket in a WebGL player (<see cref="LatencyProbe.CreateDefaultTransport"/>); tests replace it.
    /// </summary>
    public interface IWebSocketEchoTransport
    {
        /// <summary>Opens a WebSocket to <paramref name="url"/> (<c>wss://</c> or <c>ws://</c>).</summary>
        Task<IWebSocketEchoSession> ConnectAsync(string url, CancellationToken cancellationToken);
    }

    /// <summary>One open beacon connection. The probe starts a receive and then sends, so one receive and one send may be in flight at once.</summary>
    public interface IWebSocketEchoSession : IDisposable
    {
        /// <summary>Sends one text message.</summary>
        Task SendTextAsync(string text, CancellationToken cancellationToken);

        /// <summary>
        /// The next complete text message with the <see cref="System.Diagnostics.Stopwatch"/>
        /// timestamp of the socket read that completed it. Binary messages are skipped; a close throws.
        /// The timestamp must be read on the thread that completed the read, before anything yields
        /// to the Unity main thread: a main-thread <c>await</c> resumes up to a frame late.
        /// </summary>
        Task<EchoMessage> ReceiveTextAsync(CancellationToken cancellationToken);
    }

    /// <summary>A received text message and when its last socket read completed.</summary>
    public readonly struct EchoMessage
    {
        /// <summary>Creates a message.</summary>
        public EchoMessage(string text, long receivedTimestamp)
        {
            Text = text;
            ReceivedTimestamp = receivedTimestamp;
        }

        /// <summary>The message text.</summary>
        public string Text { get; }

        /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> read when the read that completed the message finished, on the completing thread.</summary>
        public long ReceivedTimestamp { get; }
    }

    /// <summary>Options for <see cref="LatencyProbe.MeasureAsync"/>.</summary>
    public sealed class LatencyProbeOptions
    {
        /// <summary>Timed samples per location after the discarded warm-up.</summary>
        public int Samples { get; set; } = 5;

        /// <summary>Each sample's timeout; one timeout omits the location.</summary>
        public TimeSpan SampleTimeout { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>The WebSocket connect timeout.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Beacon connections open at once.</summary>
        public int MaxConcurrency { get; set; } = 4;

        /// <summary>The WebSocket seam; null for the platform default (<see cref="LatencyProbe.CreateDefaultTransport"/>).</summary>
        public IWebSocketEchoTransport Transport { get; set; }
    }

    /// <summary>One location's measurement.</summary>
    public sealed class LocationLatency
    {
        /// <summary>Creates a measurement.</summary>
        public LocationLatency(string id, int? medianMs, bool clamped, string failure)
        {
            Id = id;
            MedianMs = medianMs;
            Clamped = clamped;
            Failure = failure;
        }

        /// <summary>The location id.</summary>
        public string Id { get; }

        /// <summary>The median of the samples in whole ms (never 0), or null when the location failed.</summary>
        public int? MedianMs { get; }

        /// <summary>True when a sub-millisecond median was reported as 1.</summary>
        public bool Clamped { get; }

        /// <summary>Why the location was omitted, or null.</summary>
        public string Failure { get; }

        /// <inheritdoc />
        public override string ToString() => MedianMs.HasValue ? $"{Id}={MedianMs}ms" + (Clamped ? " (clamped)" : string.Empty) : $"{Id} failed: {Failure}";
    }

    /// <summary>The result of measuring every location.</summary>
    public sealed class LatencyResult
    {
        /// <summary>Creates a result.</summary>
        public LatencyResult(DiscoveryOutcome outcome, IReadOnlyDictionary<string, int> medians, IReadOnlyList<LocationLatency> detail, string message = null)
        {
            Outcome = outcome;
            Medians = medians ?? new Dictionary<string, int>();
            Detail = detail ?? Array.Empty<LocationLatency>();
            Message = message;
        }

        /// <summary>
        /// <see cref="DiscoveryOutcome.Ok"/> when at least one location was measured or none had a
        /// beacon; <see cref="DiscoveryOutcome.Unreachable"/> when every beacon failed;
        /// <see cref="DiscoveryOutcome.Unsupported"/> where <see cref="LatencyProbe.IsSupported"/> is false (no platform
        /// today); otherwise the locations call's outcome.
        /// </summary>
        public DiscoveryOutcome Outcome { get; }

        /// <summary>The latency map for tickets, quick join and the list: measured locations only, never 0.</summary>
        public IReadOnlyDictionary<string, int> Medians { get; }

        /// <summary>Every location with a beacon, measured or failed.</summary>
        public IReadOnlyList<LocationLatency> Detail { get; }

        /// <summary>Why the outcome is not Ok, or null.</summary>
        public string Message { get; }

        /// <summary>True for <see cref="DiscoveryOutcome.Ok"/>.</summary>
        public bool IsOk => Outcome == DiscoveryOutcome.Ok;
    }

    /// <summary>The median rule of the Discovery latency procedure. Pure.</summary>
    internal static class LatencyMath
    {
        /// <summary>
        /// Every one of <paramref name="want"/> samples must be present (one timeout fails the
        /// location). The middle sample after sorting (the mean of the two middles for an even
        /// count), rounded half away from zero; a median below 1 ms is reported as 1, clamped.
        /// </summary>
        public static bool TryMedian(IReadOnlyList<double> samplesMs, int want, out int medianMs, out bool clamped)
        {
            medianMs = 0;
            clamped = false;
            if (want < 1 || samplesMs == null || samplesMs.Count != want)
            {
                return false;
            }

            var sorted = new List<double>(samplesMs);
            sorted.Sort();
            double mid = want % 2 == 1 ? sorted[want / 2] : (sorted[(want / 2) - 1] + sorted[want / 2]) / 2;
            int rounded = (int)Math.Round(mid, MidpointRounding.AwayFromZero);
            if (rounded < 1)
            {
                medianMs = 1;
                clamped = true;
                return true;
            }

            medianMs = rounded;
            return true;
        }

        /// <summary>The latency map: measured locations only; a failed or non-positive one is omitted.</summary>
        public static Dictionary<string, int> Map(IEnumerable<LocationLatency> results)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LocationLatency r in results)
            {
                if (r != null && r.Failure == null && r.MedianMs.HasValue && r.MedianMs.Value > 0)
                {
                    map[r.Id] = r.MedianMs.Value;
                }
            }

            return map;
        }
    }
}
