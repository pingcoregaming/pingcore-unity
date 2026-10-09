using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using PingCore.Core;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>A fake endpoint, a shim pointed at it through the real loopback transport, a virtual clock and a log.</summary>
    internal sealed class FleetShimHarness : IDisposable
    {
        /// <param name="holdDelays">Every scheduler delay waits for the test to release it (<see cref="TestScheduler.NextHeldDelayAsync"/>).</param>
        public FleetShimHarness(TimeSpan? healthInterval = null, int? maxLineBytes = null, bool holdDelays = false)
        {
            Scheduler = new TestScheduler(holdDelays);
            Fake = FakeLocalSdkEndpoint.Start();
            var options = new FleetSdkOptions
            {
                GetEnvironmentVariable = name => name == FleetSdk.PortVariable ? Fake.Port.ToString(CultureInfo.InvariantCulture) : null,
                Scheduler = Scheduler,
                Log = Logs.Add,
                HealthInterval = healthInterval ?? TimeSpan.Zero,
            };
            if (maxLineBytes != null)
            {
                var transport = new LoopbackHttpTransport(options.CallTimeout, maxLineBytes.Value);
                options.Transport = transport;
                options.LineStream = transport;
                OwnedTransport = transport;
            }

            Sdk = FleetSdk.Create(options);
            Sdk.StateChanged += change => { lock (States) { States.Add(change); } };
            Sdk.AllocationReceived += info => { lock (Received) { Received.Add(info); } };
            Sdk.AllocationCleared += cleared => { lock (Cleared) { Cleared.Add(cleared); } };
            Sdk.GameServerChanged += _ => ViewsApplied.Increment();
        }

        /// <summary>
        /// Views the shim applied (<see cref="IFleetSdk.GameServerChanged"/>, raised synchronously): the first is
        /// <c>StartAsync</c>'s read, which comes before the watch starts; each later one is a watch frame the shim
        /// has read, so <c>ReachedAsync(2)</c> means it has read its first frame.
        /// </summary>
        public EventCounter ViewsApplied { get; } = new EventCounter();

        public FakeLocalSdkEndpoint Fake { get; }

        public TestScheduler Scheduler { get; }

        public LogCollector Logs { get; } = new LogCollector();

        public FleetSdk Sdk { get; }

        public List<FleetStateChange> States { get; } = new List<FleetStateChange>();

        public List<AllocationInfo> Received { get; } = new List<AllocationInfo>();

        public List<AllocationCleared> Cleared { get; } = new List<AllocationCleared>();

        public CancellationToken None => CancellationToken.None;

        private IDisposable OwnedTransport { get; }

        public int ReceivedCount
        {
            get
            {
                lock (Received)
                {
                    return Received.Count;
                }
            }
        }

        public int ClearedCount
        {
            get
            {
                lock (Cleared)
                {
                    return Cleared.Count;
                }
            }
        }

        public void Dispose()
        {
            Sdk.Dispose();
            OwnedTransport?.Dispose();
            Fake.Dispose();
        }
    }

    /// <summary>A transport that fails the test if anything is sent: proves an inert shim does no I/O.</summary>
    internal sealed class ForbiddenTransport : IHttpTransport, ILineStreamTransport
    {
        public int Calls { get; private set; }

        public System.Threading.Tasks.Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("an inert shim sent " + request.Method + " " + request.Url);
        }

        public System.Threading.Tasks.Task<LineStreamResult> ReadLinesAsync(string url, Action<string> onLine, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("an inert shim opened " + url);
        }
    }

    /// <summary>
    /// A scripted endpoint with no sockets: <c>GET /gameserver</c> answers a Ready view; every other unary
    /// call throws <see cref="Failure"/>. The first watch request sends one frame and ends cleanly (an
    /// established stream closing); every later one hangs until cancelled, so the reconnect loop cannot
    /// move the virtual clock on its own.
    /// </summary>
    internal sealed class ScriptedTransport : IHttpTransport, ILineStreamTransport
    {
        public const string ReadyView = "{\"object_meta\":{\"name\":\"gameserver-42\",\"annotations\":{}},\"status\":{\"state\":\"Ready\"}}";

        private int watches;

        public Func<Exception> Failure { get; set; } = () => new System.Threading.Tasks.TaskCanceledException("timeout");

        public int Watches => Volatile.Read(ref watches);

        public System.Threading.Tasks.Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            if (request.Method == "GET" && request.Url.EndsWith("/gameserver", StringComparison.Ordinal))
            {
                return System.Threading.Tasks.Task.FromResult(new PingCoreHttpResponse(200, null, ReadyView));
            }

            throw Failure();
        }

        public async System.Threading.Tasks.Task<LineStreamResult> ReadLinesAsync(string url, Action<string> onLine, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref watches) == 1)
            {
                onLine("{\"result\":" + ReadyView + "}");
                return new LineStreamResult(200, LineStreamEnd.EndOfStream);
            }

            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, cancellationToken);
            return new LineStreamResult(200, LineStreamEnd.EndOfStream);
        }
    }

    /// <summary>A transport whose every call fails like a refused connection, at once.</summary>
    internal sealed class RefusingTransport : IHttpTransport, ILineStreamTransport
    {
        public int Calls { get; private set; }

        public System.Threading.Tasks.Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new System.Net.Http.HttpRequestException("connection refused");
        }

        public System.Threading.Tasks.Task<LineStreamResult> ReadLinesAsync(string url, Action<string> onLine, CancellationToken cancellationToken)
        {
            Calls++;
            throw new System.Net.Http.HttpRequestException("connection refused");
        }
    }
}
