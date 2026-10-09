using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Sessions
{
    /// <summary>
    /// Follows the backfills Discovery delivers into the running session. On every GameServer view
    /// whose <c>pingcore.io/backfill-id</c> annotation names a backfill it does not know yet, it reads
    /// <c>GET /v1/backfills</c> (a GET: it never integrates the game) and raises
    /// <see cref="BackfillReceived"/> once per backfill id. <see cref="WaitForAsync"/> lets a
    /// <c>backfill</c> join that outran the annotation wait for its backfill. The reads for the
    /// annotation and for every wait are single-flight: one <c>GET /v1/backfills</c> in flight at a time,
    /// shared by everyone who needs it then, so concurrent backfill joins cost one request per poll tick,
    /// not one each. A new session (another allocation id on the view) forgets the old session's
    /// backfills. Use it from the main thread.
    /// </summary>
    public sealed class BackfillWatcher : IDisposable
    {
        private readonly IFleetSdk fleet;
        private readonly IScheduler scheduler;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly Dictionary<string, BackfillContext> known = new Dictionary<string, BackfillContext>(StringComparer.Ordinal);
        private readonly HashSet<string> raised = new HashSet<string>(StringComparer.Ordinal);
        private readonly SingleFlight<BackfillsResult> sharedRead;
        private string sessionId;
        private bool refreshing;
        private bool refreshAgain;
        private bool disposed;

        /// <summary>Starts following <paramref name="fleet"/>'s GameServer views.</summary>
        /// <param name="fleet">The local SDK shim.</param>
        /// <param name="scheduler">Delays for <see cref="WaitForAsync"/>; null uses the Unity main-thread scheduler.</param>
        public BackfillWatcher(IFleetSdk fleet, IScheduler scheduler = null)
        {
            this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            this.scheduler = scheduler ?? new UnityScheduler();
            // The shared read runs on the watcher's lifetime, so one waiter's cancellation never cancels it for the others.
            sharedRead = new SingleFlight<BackfillsResult>(
                async () => Absorb(await fleet.GetBackfillsAsync(lifetime.Token)),
                e => new BackfillsResult(FleetCallOutcome.Cancelled, 0, "the backfill read ended on " + e.GetType().Name, null));
            sessionId = fleet.CurrentAllocation?.AllocationId;
            fleet.GameServerChanged += OnGameServerChanged;
        }

        /// <summary>Raised once per backfill id, after the backfill list was read, in delivery order.</summary>
        public event Action<BackfillContext> BackfillReceived;

        /// <summary>The live backfills from the last successful read, oldest first. Never null.</summary>
        public IReadOnlyList<BackfillContext> Live
        {
            get
            {
                List<BackfillContext> list;
                lock (gate)
                {
                    list = new List<BackfillContext>(known.Values);
                }

                list.Sort((a, b) => Nullable.Compare(a.DeliveredAt, b.DeliveredAt));
                return list;
            }
        }

        /// <summary>The live backfill with this allocation id, from the last successful read.</summary>
        public bool TryGet(string allocationId, out BackfillContext backfill)
        {
            backfill = null;
            if (allocationId == null)
            {
                return false;
            }

            lock (gate)
            {
                return known.TryGetValue(allocationId, out backfill);
            }
        }

        /// <summary>
        /// Reads <c>GET /v1/backfills</c> now, on its own request (not the shared one): the live set
        /// replaces the known one and every new id is raised. A failed read keeps what was known.
        /// </summary>
        public async Task<BackfillsResult> RefreshAsync(CancellationToken cancellationToken)
        {
            BackfillsResult result = await fleet.GetBackfillsAsync(cancellationToken);
            return Absorb(result);
        }

        /// <summary>Applies one read: the live set replaces the known one and every new id is raised. A failed read keeps what was known.</summary>
        private BackfillsResult Absorb(BackfillsResult result)
        {
            if (disposed || !result.IsOk)
            {
                return result;
            }

            var fresh = new List<BackfillContext>();
            lock (gate)
            {
                known.Clear();
                foreach (BackfillView view in result.Backfills)
                {
                    BackfillContext backfill = BackfillContext.Parse(view);
                    if (backfill == null)
                    {
                        continue;
                    }

                    known[backfill.AllocationId] = backfill;
                    if (raised.Add(backfill.AllocationId))
                    {
                        fresh.Add(backfill);
                    }
                }
            }

            foreach (BackfillContext backfill in fresh)
            {
                try
                {
                    BackfillReceived?.Invoke(backfill);
                }
                catch (Exception)
                {
                    // A subscriber's failure must not stop the others or the watcher.
                }
            }

            return result;
        }

        /// <summary>
        /// The backfill with <paramref name="allocationId"/>: at once when it is known, else asking
        /// <c>GET /v1/backfills</c> every <paramref name="poll"/> for up to <paramref name="wait"/> of
        /// scheduler time, joining a read already in flight instead of sending another. Null when it never
        /// appears, or on cancellation.
        /// </summary>
        public async Task<BackfillContext> WaitForAsync(string allocationId, TimeSpan wait, TimeSpan poll, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(allocationId))
            {
                return null;
            }

            if (poll <= TimeSpan.Zero)
            {
                poll = TimeSpan.FromMilliseconds(500);
            }

            DateTimeOffset deadline = scheduler.UtcNow + wait;
            while (true)
            {
                if (TryGet(allocationId, out BackfillContext found))
                {
                    return found;
                }

                if (cancellationToken.IsCancellationRequested || disposed)
                {
                    return null;
                }

                try
                {
                    await sharedRead.RunAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (TryGet(allocationId, out found))
                {
                    return found;
                }

                TimeSpan remaining = deadline - scheduler.UtcNow;
                if (remaining <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                try
                {
                    await scheduler.DelayAsync(remaining < poll ? remaining : poll, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        /// <summary>Stops following the shim. Idempotent.</summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            fleet.GameServerChanged -= OnGameServerChanged;
            lifetime.Cancel();
            lifetime.Dispose();
        }

        private void OnGameServerChanged(GameServerSnapshot snapshot)
        {
            if (disposed || snapshot == null)
            {
                return;
            }

            bool unknownBackfill;
            lock (gate)
            {
                if (!string.Equals(snapshot.AllocationId, sessionId, StringComparison.Ordinal))
                {
                    sessionId = snapshot.AllocationId;
                    known.Clear();
                    raised.Clear();
                }

                unknownBackfill = snapshot.BackfillId != null && !known.ContainsKey(snapshot.BackfillId);
            }

            if (unknownBackfill)
            {
                _ = RefreshFromAnnotationAsync();
            }
        }

        private async Task RefreshFromAnnotationAsync()
        {
            lock (gate)
            {
                if (refreshing)
                {
                    refreshAgain = true;
                    return;
                }

                refreshing = true;
            }

            try
            {
                bool again;
                do
                {
                    lock (gate)
                    {
                        refreshAgain = false;
                    }

                    await sharedRead.RunAsync(lifetime.Token);
                    lock (gate)
                    {
                        again = refreshAgain && !disposed;
                    }
                }
                while (again);
            }
            catch (Exception)
            {
                // GetBackfillsAsync never throws for HTTP or transport failures; anything else is a disposal race.
            }
            finally
            {
                lock (gate)
                {
                    refreshing = false;
                }
            }
        }
    }
}
