using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Sessions
{
    /// <summary>
    /// Keeps a running session's joinable record in step with its seats, so the matchmaker can backfill
    /// it: publishes <see cref="JoinablePlan.OpenSeats"/> when the count changes and again every
    /// <c>ttlSeconds / 2</c>, withdraws at 0, and withdraws on <see cref="Stop"/>. Nothing is published
    /// before the first <see cref="Update"/>. When the allocation clears (the game ended the session,
    /// which withdraws the record, or the platform cleared it) the keeper stops on its own.
    /// <para>
    /// A 2xx is only local acceptance. The supervisor stores the record and forwards it to Discovery,
    /// but Discovery can still refuse it (for example more open seats than the <c>players</c> capacity
    /// it knows of), and today the game is never told: the refusal shows only in the matchmaking
    /// inspector, which needs a backend-scope token no game holds. The clamp to the counter's free
    /// capacity keeps the keeper's own records inside what Discovery accepts; anything else is not
    /// guaranteed by a <see cref="JoinablePublishResult.LocallyAccepted"/> result.
    /// </para>
    /// Use it from the main thread.
    /// </summary>
    public sealed partial class JoinableSessionKeeper : IDisposable
    {
        private readonly IFleetSdk fleet;
        private readonly IScheduler scheduler;
        private readonly object gate = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private TaskCompletionSource<bool> wake = NewWake();
        private JoinableState state;
        private int connected;
        private int expectedJoiners;
        private bool updated;
        private bool stopped;
        private Task loop;

        private JoinableSessionKeeper(IFleetSdk fleet, string allocationId, string queue, int sessionSize, int maxPlayers, int ttlSeconds, IScheduler scheduler)
        {
            this.fleet = fleet;
            this.scheduler = scheduler;
            AllocationId = allocationId;
            Queue = queue;
            SessionSize = sessionSize;
            MaxPlayers = maxPlayers;
            TtlSeconds = ttlSeconds;
        }

        /// <summary>Raised after every publish attempt, with its result.</summary>
        public event Action<JoinablePublishResult> Published;

        /// <summary>Raised after every withdraw attempt, with its result.</summary>
        public event Action<FleetCallResult> Withdrawn;

        /// <summary>The session (allocation id) whose record this keeps.</summary>
        public string AllocationId { get; }

        /// <summary>The matchmaker queue the record names.</summary>
        public string Queue { get; }

        /// <summary>The session size the record names; 0 or less leaves it out.</summary>
        public int SessionSize { get; }

        /// <summary>The game's own seat limit.</summary>
        public int MaxPlayers { get; }

        /// <summary>The record's lifetime; republished every half of it.</summary>
        public int TtlSeconds { get; }

        /// <summary>The result of the last publish attempt, or null before the first.</summary>
        public JoinablePublishResult LastPublish { get; private set; }

        /// <summary>The open seats of the last publish the supervisor accepted, or null when no record is live (never published, withdrawn, or failed).</summary>
        public int? LiveSeats
        {
            get
            {
                lock (gate)
                {
                    return state.LiveSeats;
                }
            }
        }

        /// <summary>The open seats the inputs give now (<see cref="JoinablePlan.OpenSeats"/>).</summary>
        public int OpenSeats
        {
            get
            {
                lock (gate)
                {
                    return ComputeSeatsLocked();
                }
            }
        }

        /// <summary>False after <see cref="Stop"/>, or once the allocation cleared or the shim is inert.</summary>
        public bool IsRunning
        {
            get
            {
                lock (gate)
                {
                    return !stopped;
                }
            }
        }

        /// <summary>
        /// Starts keeping the joinable record of <paramref name="allocationId"/>. Nothing is sent until
        /// the first <see cref="Update"/>.
        /// </summary>
        /// <param name="fleet">The local SDK shim.</param>
        /// <param name="allocationId">The open session's allocation id.</param>
        /// <param name="queue">The matchmaker queue backfill tickets come from.</param>
        /// <param name="sessionSize">The session size to announce; 0 or less leaves it out.</param>
        /// <param name="maxPlayers">The game's own seat limit.</param>
        /// <param name="ttlSeconds">The record lifetime, at least 5 (the supervisor's and Discovery's floor).</param>
        /// <param name="scheduler">Delays; null uses the Unity main-thread scheduler.</param>
        public static JoinableSessionKeeper Start(IFleetSdk fleet, string allocationId, string queue, int sessionSize, int maxPlayers, int ttlSeconds = 30, IScheduler scheduler = null)
        {
            if (fleet == null)
            {
                throw new ArgumentNullException(nameof(fleet));
            }

            if (string.IsNullOrEmpty(allocationId))
            {
                throw new ArgumentException("allocationId is required", nameof(allocationId));
            }

            if (maxPlayers < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers), "maxPlayers must be 0 or more");
            }

            if (ttlSeconds < 5)
            {
                throw new ArgumentOutOfRangeException(nameof(ttlSeconds), "ttlSeconds must be at least 5");
            }

            var keeper = new JoinableSessionKeeper(fleet, allocationId, queue, sessionSize, maxPlayers, ttlSeconds, scheduler ?? new UnityScheduler());
            fleet.AllocationCleared += keeper.OnAllocationCleared;
            keeper.loop = keeper.RunAsync();
            return keeper;
        }

        /// <summary>
        /// The current seats: players connected to the session, and players expected but not yet
        /// connected (the backfill roster entries still on their way). Republishes when the open seats change.
        /// </summary>
        public void Update(int connected, int expectedJoiners)
        {
            lock (gate)
            {
                if (stopped)
                {
                    return;
                }

                this.connected = connected;
                this.expectedJoiners = expectedJoiners;
                updated = true;
                wake.TrySetResult(true);
            }
        }

        /// <summary>Stops the keeper and withdraws the record (best effort, not awaited). Idempotent.</summary>
        public void Stop() => _ = StopAsync(CancellationToken.None);

        /// <summary>Stops the keeper and withdraws the record if one may be live; the withdraw's result, or null when nothing was owed or it already stopped.</summary>
        public async Task<FleetCallResult> StopAsync(CancellationToken cancellationToken)
        {
            Task running;
            lock (gate)
            {
                if (stopped)
                {
                    return null;
                }

                stopped = true;
                running = loop;
                wake.TrySetResult(true);
            }

            fleet.AllocationCleared -= OnAllocationCleared;
            lifetime.Cancel();
            if (running != null)
            {
                // Let an in-flight publish finish first, so its record is known to be owed a withdraw.
                await running;
            }

            bool owed;
            lock (gate)
            {
                owed = state.LiveSeats.HasValue || state.MaybeLive;
            }

            if (!owed)
            {
                return null;
            }

            FleetCallResult result = await fleet.WithdrawJoinableAsync(AllocationId, cancellationToken);
            lock (gate)
            {
                if (result.IsOk)
                {
                    state.LiveSeats = null;
                    state.MaybeLive = false;
                }
            }

            RaiseWithdrawn(result);
            return result;
        }

        /// <summary>Stops without withdrawing (for example the session is ending, which withdraws the record anyway).</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (stopped)
                {
                    return;
                }

                stopped = true;
                wake.TrySetResult(true);
            }

            fleet.AllocationCleared -= OnAllocationCleared;
            lifetime.Cancel();
        }

        private static TaskCompletionSource<bool> NewWake() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private int ComputeSeatsLocked()
        {
            GameServerCounter players = null;
            fleet.Current?.Counters.TryGetValue("players", out players);
            return JoinablePlan.OpenSeats(MaxPlayers, connected, expectedJoiners, players?.Capacity, players?.Count);
        }

        private void OnAllocationCleared(AllocationCleared cleared)
        {
            if (cleared != null && string.Equals(cleared.AllocationId, AllocationId, StringComparison.Ordinal))
            {
                Dispose();
            }
        }
    }
}
