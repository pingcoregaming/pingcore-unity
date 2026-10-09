using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;

namespace PingCore.Fleet
{
    /// <summary>
    /// The local SDK shim (see <see cref="IFleetSdk"/>). Create it once per process with
    /// <see cref="Create"/>; it is inert when <c>AGONES_SDK_HTTP_PORT</c> is unset or not a port
    /// number. This file holds creation, state, view application and failure classification. The
    /// lifecycle calls (start, ready, shutdown, stopping) are in
    /// <c>FleetSdk.Lifecycle.cs</c>, the watch stream in <c>FleetSdk.Watch.cs</c>, the health pings in
    /// <c>FleetSdk.Health.cs</c>, logging and event raising in <c>FleetSdk.Logging.cs</c>, the self-allocation in
    /// <c>FleetSdk.SelfAllocation.cs</c>, and the other calls in <c>FleetSdk.Calls.cs</c>.
    /// </summary>
    public sealed partial class FleetSdk : IFleetSdk
    {
        /// <summary>The environment variable naming the local SDK endpoint's port; the only one the shim reads.</summary>
        public const string PortVariable = LocalSdkValues.PortVariable;

        /// <summary>Any transport failure within this long after an established watch stream closed is <see cref="FleetCallOutcome.EndpointClosed"/>.</summary>
        internal static readonly TimeSpan EndpointClosedGrace = TimeSpan.FromSeconds(5);

        private readonly object gate = new object();
        private readonly FleetSdkOptions options;
        private readonly Action<FleetLogEntry> log;
        private readonly IScheduler scheduler;
        private readonly ILineStreamTransport lineStream;
        private readonly LocalSdkCaller caller;
        private readonly IDisposable ownedTransport;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly AllocationTracker allocations = new AllocationTracker();

        private FleetState state;
        private GameServerSnapshot current;
        private bool disposed;
        private DateTimeOffset? stoppingAt;

        private FleetSdk(FleetSdkOptions options, Action<FleetLogEntry> log)
        {
            this.options = options;
            this.log = log;
            state = FleetState.Inert;
        }

        private FleetSdk(FleetSdkOptions options, Action<FleetLogEntry> log, int port)
        {
            this.options = options;
            this.log = log;
            Port = port;
            state = FleetState.Starting;
            scheduler = options.Scheduler ?? new UnityScheduler();

            LoopbackHttpTransport loopback = null;
            if (options.Transport == null || options.LineStream == null)
            {
                loopback = new LoopbackHttpTransport(options.CallTimeout);
                ownedTransport = loopback;
            }

            IHttpTransport transport = options.Transport ?? loopback;
            lineStream = options.LineStream ?? loopback;
            caller = new LocalSdkCaller(transport, "http://127.0.0.1:" + port, ClassifyTransportFailure, lifetime.Token);
        }

        /// <inheritdoc />
        public event Action<FleetStateChange> StateChanged;

        /// <inheritdoc />
        public event Action<GameServerSnapshot> GameServerChanged;

        /// <inheritdoc />
        public event Action<AllocationInfo> AllocationReceived;

        /// <inheritdoc />
        public event Action<AllocationCleared> AllocationCleared;

        /// <inheritdoc />
        public bool IsHosted => Port > 0;

        /// <summary>The local SDK endpoint's port, or 0 when inert.</summary>
        public int Port { get; }

        /// <inheritdoc />
        public FleetState State
        {
            get
            {
                lock (gate)
                {
                    return state;
                }
            }
        }

        /// <inheritdoc />
        public GameServerSnapshot Current
        {
            get
            {
                lock (gate)
                {
                    return current;
                }
            }
        }

        /// <inheritdoc />
        public AllocationInfo CurrentAllocation
        {
            get
            {
                lock (gate)
                {
                    return allocations.Current;
                }
            }
        }

        /// <summary>
        /// Creates the shim. Reads <c>AGONES_SDK_HTTP_PORT</c> through
        /// <see cref="FleetSdkOptions.GetEnvironmentVariable"/>; unset or not a port number
        /// (1 to 65535) gives an inert instance that never constructs a transport or does I/O.
        /// </summary>
        public static FleetSdk Create(FleetSdkOptions options = null)
        {
            options = options ?? new FleetSdkOptions();
            Action<FleetLogEntry> log = options.Log ?? UnityFleetLog.Write;
            Func<string, string> environment = options.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
            string raw = environment(LocalSdkValues.PortVariable);
            if (!LocalSdkValues.TryParsePort(raw, out int port))
            {
                if (!string.IsNullOrEmpty(raw))
                {
                    SafeLog(log, new FleetLogEntry(FleetLogLevel.Warning, "create", PortVariable + " is set but is not a port number from 1 to 65535; the local SDK shim is inert", 0, null));
                }

                return new FleetSdk(options, log);
            }

            return new FleetSdk(options, log, port);
        }

        /// <summary>Cancels the watch, the health pings and every call in flight, and releases the default transport.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            lifetime.Cancel();
            StopWatch();
            ownedTransport?.Dispose();
        }

        /// <summary>Applies a view under the lock, then raises the events outside it.</summary>
        private void ApplyView(GameServerSnapshot snapshot, FleetInputKind kind, string cause)
        {
            AllocationChange change;
            FleetStateChange transition;
            bool selfReplaced;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                current = snapshot;
                change = allocations.Observe(snapshot.AllocationId, snapshot.AllocationContextJson, scheduler.UtcNow);
                selfReplaced = ObserveSelfAllocationLocked(change);
                transition = TransitionLocked(FleetInput.View(kind, snapshot), cause);
            }

            if (change.ContextInvalid)
            {
                Log(FleetLogLevel.Warning, "allocation", "the allocation context annotation is not a JSON object; AllocationInfo.Context is empty and ContextInvalid is set, so match joins fail closed", 0, null);
            }

            Raise(GameServerChanged, snapshot, nameof(GameServerChanged));
            if (change.Cleared != null)
            {
                Log(FleetLogLevel.Info, "allocation", "allocation cleared (" + change.Cleared.Reason + ")", 0, null);
                Raise(AllocationCleared, change.Cleared, nameof(AllocationCleared));
            }

            if (selfReplaced)
            {
                Log(FleetLogLevel.Warning, "allocation", "a platform allocation replaced this game server's own self-allocation " + change.Cleared.AllocationId
                    + "; that session's players are not in the new one, and the supervisor may still count the replaced session", 0, null);
            }

            if (change.Received != null)
            {
                Log(FleetLogLevel.Info, "allocation", "allocation received", 0, null);
                Raise(AllocationReceived, change.Received, nameof(AllocationReceived));
            }

            if (transition != null)
            {
                Raise(StateChanged, transition, nameof(StateChanged));
            }
        }

        private void Transition(FleetInput input, string cause)
        {
            FleetStateChange transition;
            lock (gate)
            {
                transition = TransitionLocked(input, cause);
            }

            if (transition != null)
            {
                Raise(StateChanged, transition, nameof(StateChanged));
            }
        }

        private FleetStateChange TransitionLocked(FleetInput input, string cause)
        {
            FleetState next = FleetStateMachine.Next(state, input);
            if (next == state)
            {
                return null;
            }

            var transition = new FleetStateChange(state, next, cause);
            state = next;
            if (!HealthCadence.ShouldPing(next))
            {
                StopHealthPingsLocked();
            }

            return transition;
        }

        /// <summary>The outcome of a transport failure (<see cref="TransportFailure.Outcome"/>), from the shim's stop and watch signals.</summary>
        private FleetCallOutcome ClassifyTransportFailure(bool answeredBefore, TransportFailureKind kind)
        {
            bool stopping;
            bool inWatchClosedWindow;
            lock (gate)
            {
                stopping = stoppingAt != null;
                inWatchClosedWindow = !watchConnected && watchClosedAt != null && scheduler.UtcNow - watchClosedAt.Value <= EndpointClosedGrace;
            }

            return TransportFailure.Outcome(stopping, inWatchClosedWindow, answeredBefore, kind);
        }

        private static FleetCallResult InertResult() => new FleetCallResult(FleetCallOutcome.Inert, 0, "not a hosted game server");
    }
}
