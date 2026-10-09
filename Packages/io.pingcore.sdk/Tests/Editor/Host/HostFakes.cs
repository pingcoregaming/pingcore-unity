using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;

namespace PingCore.Discovery.Host.Tests.Editor
{
    /// <summary>
    /// A manual clock: a delay completes only when <see cref="Advance"/> moves the clock to its target,
    /// so a test decides exactly when each heartbeat is due. Every requested delay is recorded, and a
    /// cancelled delay leaves the pending list at once. Continuations run asynchronously.
    /// </summary>
    internal sealed class ManualScheduler : IScheduler
    {
        private readonly object gate = new object();
        private readonly List<Pending> pending = new List<Pending>();
        private readonly List<TimeSpan> delays = new List<TimeSpan>();
        private DateTimeOffset now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (gate)
                {
                    return now;
                }
            }
        }

        /// <summary>Every delay requested, in order.</summary>
        public List<TimeSpan> Delays
        {
            get
            {
                lock (gate)
                {
                    return delays.ToList();
                }
            }
        }

        public int PendingCount
        {
            get
            {
                lock (gate)
                {
                    return pending.Count;
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new Pending { Source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            lock (gate)
            {
                delays.Add(delay);
                entry.At = now + delay;
                pending.Add(entry);
            }

            cancellationToken.Register(() =>
            {
                lock (gate)
                {
                    pending.Remove(entry);
                }

                entry.Source.TrySetCanceled();
            });
            return entry.Source.Task;
        }

        /// <summary>Moves the clock and completes every delay that is now due.</summary>
        public void Advance(TimeSpan by)
        {
            List<Pending> due;
            lock (gate)
            {
                now += by;
                due = pending.Where(p => p.At <= now).ToList();
                foreach (Pending p in due)
                {
                    pending.Remove(p);
                }
            }

            foreach (Pending p in due)
            {
                p.Source.TrySetResult(true);
            }
        }

        private sealed class Pending
        {
            public DateTimeOffset At;
            public TaskCompletionSource<bool> Source;
        }
    }

    /// <summary>
    /// A Discovery stand-in behind <see cref="IHttpTransport"/>: records every request and answers
    /// from a queue of responders, then from <see cref="Default"/>.
    /// </summary>
    internal sealed class RecordingTransport : IHttpTransport
    {
        private readonly object gate = new object();
        private readonly List<PingCoreHttpRequest> requests = new List<PingCoreHttpRequest>();
        private readonly Queue<Func<PingCoreHttpRequest, PingCoreHttpResponse>> queued = new Queue<Func<PingCoreHttpRequest, PingCoreHttpResponse>>();

        public Func<PingCoreHttpRequest, PingCoreHttpResponse> Default { get; set; } = _ => HostResponses.HeartbeatOk();

        public List<PingCoreHttpRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return requests.ToList();
                }
            }
        }

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return requests.Count;
                }
            }
        }

        public void Enqueue(Func<PingCoreHttpRequest, PingCoreHttpResponse> responder)
        {
            lock (gate)
            {
                queued.Enqueue(responder);
            }
        }

        public void Enqueue(PingCoreHttpResponse response) => Enqueue(_ => response);

        public Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Func<PingCoreHttpRequest, PingCoreHttpResponse> responder;
            lock (gate)
            {
                requests.Add(request);
                responder = queued.Count > 0 ? queued.Dequeue() : Default;
            }

            return Task.FromResult(responder(request));
        }
    }

    /// <summary>Canned Discovery answers, shaped like the route code and the pinned fixtures.</summary>
    internal static class HostResponses
    {
        public const string BaseUrl = "https://discovery.test";
        public const string ServerId = "203.0.113.10:27015";

        // Short on purpose: a token-shaped literal (16+ characters after the prefix) would trip the build guard's scan.
        public const string Token = "dsc_hostfake";

        public static PingCoreHttpResponse Json(int status, string body, IReadOnlyDictionary<string, string> headers = null)
        {
            return new PingCoreHttpResponse(status, headers ?? new Dictionary<string, string>(), body);
        }

        public static PingCoreHttpResponse HeartbeatOk(string serverId = ServerId, string verified = "pending")
        {
            string verifiedJson = verified == null ? "null" : "\"" + verified + "\"";
            return Json(200, "{\"error\":false,\"serverId\":\"" + serverId + "\",\"ip\":\"203.0.113.10\",\"expiresIn\":90,\"verificationMode\":\"udp-echo\",\"verified\":" + verifiedJson + ",\"lastProbeError\":null}");
        }

        public static PingCoreHttpResponse Refusal(string reason, int limit)
        {
            return Json(409, "{\"error\":true,\"message\":\"refused\",\"reason\":\"" + reason + "\",\"limit\":" + limit + "}");
        }

        public static PingCoreHttpResponse RateLimited(int retryAfterSeconds)
        {
            return Json(429, "{\"error\":true,\"message\":\"Too many requests.\"}", new Dictionary<string, string> { ["Retry-After"] = retryAfterSeconds.ToString() });
        }

        public static PingCoreHttpResponse Degraded() => Json(503, "{\"error\":true,\"message\":\"Discovery is degraded.\"}");

        public static PingCoreHttpResponse Delisted(string serverId = ServerId) => Json(200, "{\"error\":false,\"serverId\":\"" + serverId + "\",\"removed\":true}");
    }

    /// <summary>Collects log entries.</summary>
    internal sealed class HostLog
    {
        private readonly object gate = new object();
        private readonly List<HeartbeatLogEntry> entries = new List<HeartbeatLogEntry>();

        public void Add(HeartbeatLogEntry entry)
        {
            lock (gate)
            {
                entries.Add(entry);
            }
        }

        public List<HeartbeatLogEntry> Entries
        {
            get
            {
                lock (gate)
                {
                    return entries.ToList();
                }
            }
        }

        public int Warnings(string contains) => Entries.Count(e => e.Level == HeartbeatLogLevel.Warning && e.Message.Contains(contains));
    }

    internal static class HostWait
    {
        /// <summary>Polls <paramref name="condition"/> on real time until it holds, yielding so continuations run.</summary>
        public static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeoutMs)
                {
                    Assert.Fail("timed out waiting for " + what);
                }

                await Task.Delay(2);
            }
        }

        /// <summary>Yields for <paramref name="ms"/> of real time, so a wrongly scheduled send would have happened.</summary>
        public static Task Settle(int ms = 50) => Task.Delay(ms);
    }

    /// <summary>A reporter wired to the fakes.</summary>
    internal sealed class ReporterHarness
    {
        public readonly ManualScheduler Scheduler = new ManualScheduler();
        public readonly RecordingTransport Transport = new RecordingTransport();
        public readonly HostLog Log = new HostLog();
        public readonly List<HeartbeatResult> Beats = new List<HeartbeatResult>();
        public readonly List<string> EnvironmentReads = new List<string>();
        public readonly Queue<double> Units = new Queue<double>();
        public string AgonesPort;

        public HeartbeatReporter Create(Action<HeartbeatReporterOptions> configure = null)
        {
            var options = new HeartbeatReporterOptions
            {
                BaseUrl = HostResponses.BaseUrl,
                Token = HostResponses.Token,
                Transport = Transport,
                Scheduler = Scheduler,
                Name = "Beacon Rush test",
                GamePort = 7777,
                MaxPlayers = 8,
                Log = Log.Add,
                EnvironmentVariable = name =>
                {
                    lock (EnvironmentReads)
                    {
                        EnvironmentReads.Add(name);
                    }

                    return name == "AGONES_SDK_HTTP_PORT" ? AgonesPort : null;
                },
                RandomUnit = () => Units.Count > 0 ? Units.Dequeue() : 0.5,
            };
            configure?.Invoke(options);
            HeartbeatReporter reporter = HeartbeatReporter.Create(options);
            reporter.Beat += b =>
            {
                lock (Beats)
                {
                    Beats.Add(b);
                }
            };
            return reporter;
        }

        /// <summary>Waits until the loop has requested its next delay after <paramref name="sends"/> sends.</summary>
        public Task UntilWaiting(int sends) => HostWait.Until(() => Transport.Count == sends && Scheduler.PendingCount == 1, sends + " sends and a pending delay");

        public TimeSpan LastDelay => Scheduler.Delays.Last();
    }
}
