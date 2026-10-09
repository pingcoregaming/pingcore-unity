using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Fleet.Sessions;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Tests.Editor.Sessions
{
    /// <summary>
    /// <see cref="BackfillWatcher"/>'s reads are single-flight: concurrent backfill waits share one
    /// <c>GET /v1/backfills</c> per poll tick. Driven through a stub shim whose reads the test answers by
    /// hand and a manual clock, so every tick and every request is counted exactly.
    /// </summary>
    public sealed class BackfillWatcherFlightTests
    {
        private static readonly TimeSpan Wait5 = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

        [Test]
        public async Task TwoConcurrentBackfillWaitsShareOneReadPerPollTick()
        {
            var clock = new ManualClock();
            var fleet = new StubFleet();
            using (var watcher = new BackfillWatcher(fleet, clock))
            {
                Task<BackfillContext> first = watcher.WaitForAsync("bf-1", Wait5, Poll, CancellationToken.None);
                Task<BackfillContext> second = watcher.WaitForAsync("bf-1", Wait5, Poll, CancellationToken.None);

                for (int tick = 1; tick <= 3; tick++)
                {
                    await Wait.Until(() => fleet.Reads.Count >= tick, "read " + tick);
                    await Wait.For(30);
                    // Mutation: call fleet.GetBackfillsAsync directly in WaitForAsync and two reads go out per tick.
                    Assert.That(fleet.Reads.Count, Is.EqualTo(tick), "one read for both waits on tick " + tick);
                    fleet.Answer(tick - 1);
                    await Wait.Until(() => clock.Pending == 2, "both waits to sleep until the next tick");
                    clock.Advance(Poll);
                }

                await Wait.Until(() => fleet.Reads.Count >= 4, "read 4");
                Assert.That(fleet.Reads.Count, Is.EqualTo(4));
                fleet.Answer(3, "bf-1");
                BackfillContext[] found = await Task.WhenAll(first, second);
                Assert.That(found.Select(b => b?.AllocationId), Is.EqualTo(new[] { "bf-1", "bf-1" }));
                Assert.That(fleet.Reads.Count, Is.EqualTo(4), "four ticks, four reads, for two waits");
                Assert.That(fleet.Reads.All(r => !r.Token.IsCancellationRequested), Is.True);
            }
        }

        [Test]
        public async Task CancellingOneWaitLeavesTheSharedReadToTheOther()
        {
            var clock = new ManualClock();
            var fleet = new StubFleet();
            using (var watcher = new BackfillWatcher(fleet, clock))
            using (var cancel = new CancellationTokenSource())
            {
                Task<BackfillContext> leaving = watcher.WaitForAsync("bf-1", Wait5, Poll, cancel.Token);
                Task<BackfillContext> staying = watcher.WaitForAsync("bf-1", Wait5, Poll, CancellationToken.None);
                await Wait.Until(() => fleet.Reads.Count >= 1, "the shared read");
                await Wait.For(30);
                // Mutation: read directly in WaitForAsync and each wait sends its own request.
                Assert.That(fleet.Reads.Count, Is.EqualTo(1), "both waits share one read");

                cancel.Cancel();
                Assert.That(await leaving, Is.Null, "the cancelled wait ends at once");
                Assert.That(fleet.Reads[0].Token.IsCancellationRequested, Is.False, "the read the other wait shares is not cancelled");
                Assert.That(staying.IsCompleted, Is.False);

                fleet.Answer(0, "bf-1");
                Assert.That((await staying)?.AllocationId, Is.EqualTo("bf-1"));
                Assert.That(fleet.Reads.Count, Is.EqualTo(1));
            }
        }

        /// <summary>A clock whose delays complete only when <see cref="Advance"/> passes their due time.</summary>
        private sealed class ManualClock : IScheduler
        {
            private readonly object gate = new object();
            private readonly List<(DateTimeOffset Due, TaskCompletionSource<bool> Done)> waits = new List<(DateTimeOffset, TaskCompletionSource<bool>)>();
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

            public int Pending
            {
                get
                {
                    lock (gate)
                    {
                        return waits.Count(w => !w.Done.Task.IsCompleted);
                    }
                }
            }

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => done.TrySetCanceled());
                lock (gate)
                {
                    waits.Add((now + delay, done));
                }

                return done.Task;
            }

            public void Advance(TimeSpan by)
            {
                List<TaskCompletionSource<bool>> due;
                lock (gate)
                {
                    now += by;
                    due = waits.Where(w => w.Due <= now).Select(w => w.Done).ToList();
                    waits.RemoveAll(w => w.Due <= now);
                }

                foreach (TaskCompletionSource<bool> done in due)
                {
                    done.TrySetResult(true);
                }
            }
        }

        /// <summary>A shim stub: only <see cref="GetBackfillsAsync"/> works, and each read waits for <see cref="Answer"/>.</summary>
        private sealed class StubFleet : IFleetSdk
        {
            private readonly object gate = new object();
            private readonly List<(CancellationToken Token, TaskCompletionSource<BackfillsResult> Done)> reads = new List<(CancellationToken, TaskCompletionSource<BackfillsResult>)>();

            public event Action<FleetStateChange> StateChanged { add { } remove { } }

            public event Action<GameServerSnapshot> GameServerChanged { add { } remove { } }

            public event Action<AllocationInfo> AllocationReceived { add { } remove { } }

            public event Action<AllocationCleared> AllocationCleared { add { } remove { } }

            public bool IsHosted => true;

            public FleetState State => FleetState.InSession;

            public GameServerSnapshot Current => null;

            public AllocationInfo CurrentAllocation => null;

            public List<(CancellationToken Token, TaskCompletionSource<BackfillsResult> Done)> Reads
            {
                get
                {
                    lock (gate)
                    {
                        return reads.ToList();
                    }
                }
            }

            public void Answer(int index, params string[] backfillIds)
            {
                List<BackfillView> views = backfillIds.Select(id => new BackfillView { AllocationId = id, SessionId = "alloc-1" }).ToList();
                Reads[index].Done.TrySetResult(new BackfillsResult(FleetCallOutcome.Ok, 200, null, views));
            }

            public Task<BackfillsResult> GetBackfillsAsync(CancellationToken cancellationToken)
            {
                var done = new TaskCompletionSource<BackfillsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate)
                {
                    reads.Add((cancellationToken, done));
                }

                return done.Task;
            }

            public Task<bool> StartAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<FleetCallResult> ReadyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<CounterResult> SetCounterAsync(string name, long count, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<CounterResult> GetCounterAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<FleetCallResult> EndSessionAsync(string allocationId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<JoinablePublishResult> PublishJoinableAsync(string allocationId, JoinableSessionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<FleetCallResult> WithdrawJoinableAsync(string allocationId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<ReservationLookup> GetReservationAsync(string reservationId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<ReservationsResult> ListReservationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<FleetCallResult> ShutdownAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<SelfAllocationResult> AllocateSelfAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

            public void NotifyProcessStopping()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
