using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// A manual virtual clock. <see cref="DelayAsync"/> never completes by itself: it waits until the
    /// test moves time with <see cref="Advance"/> (or <see cref="RunAsync{T}"/>, which jumps to the
    /// earliest pending delay whenever the code under test is idle). Every requested delay is
    /// recorded with the virtual time it was requested at. Cancelling a delay removes it at once.
    /// Public so the Host suite can reuse it.
    /// </summary>
    public sealed class TestScheduler : IScheduler
    {
        private readonly object gate = new object();
        private readonly List<Pending> pending = new List<Pending>();
        private readonly List<(DateTimeOffset At, TimeSpan Delay)> requested = new List<(DateTimeOffset, TimeSpan)>();
        private DateTimeOffset now;

        /// <summary>Starts the clock at <paramref name="start"/> (default 2026-10-05T12:00:00Z).</summary>
        public TestScheduler(DateTimeOffset? start = null)
        {
            now = start ?? new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        }

        /// <inheritdoc />
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

        /// <summary>Every delay requested so far, in order.</summary>
        public List<TimeSpan> Delays
        {
            get
            {
                lock (gate)
                {
                    return requested.Select(r => r.Delay).ToList();
                }
            }
        }

        /// <summary>Every delay requested so far with the virtual time it was requested at.</summary>
        public List<(DateTimeOffset At, TimeSpan Delay)> Requested
        {
            get
            {
                lock (gate)
                {
                    return requested.ToList();
                }
            }
        }

        /// <summary>Delays not yet completed or cancelled.</summary>
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

        /// <inheritdoc />
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            var entry = new Pending(new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (gate)
            {
                requested.Add((now, delay));
                entry.Due = now + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
                pending.Add(entry);
            }

            if (cancellationToken.CanBeCanceled)
            {
                entry.Registration = cancellationToken.Register(() =>
                {
                    lock (gate)
                    {
                        pending.Remove(entry);
                    }

                    entry.Completion.TrySetCanceled(cancellationToken);
                });
            }

            return entry.Completion.Task;
        }

        /// <summary>Moves the clock forward and completes every delay now due, earliest first.</summary>
        public void Advance(TimeSpan by)
        {
            List<Pending> due;
            lock (gate)
            {
                now += by;
                due = pending.Where(p => p.Due <= now).OrderBy(p => p.Due).ToList();
                foreach (Pending p in due)
                {
                    pending.Remove(p);
                }
            }

            foreach (Pending p in due)
            {
                p.Registration.Dispose();
                p.Completion.TrySetResult(true);
            }
        }

        /// <summary>Moves the clock to the earliest pending delay and completes it. False when none is pending.</summary>
        public bool AdvanceToNext()
        {
            TimeSpan step;
            lock (gate)
            {
                if (pending.Count == 0)
                {
                    return false;
                }

                DateTimeOffset due = pending.Min(p => p.Due);
                step = due > now ? due - now : TimeSpan.Zero;
            }

            Advance(step);
            return true;
        }

        /// <summary>
        /// Awaits <paramref name="work"/>, jumping virtual time to the next pending delay whenever the
        /// code under test is idle (<paramref name="idleTicks"/> polls of 2 ms real time with a delay pending). Fails the test after
        /// <paramref name="realTimeoutMs"/> of real time or <paramref name="maxSteps"/> jumps.
        /// </summary>
        public async Task<T> RunAsync<T>(Task<T> work, int realTimeoutMs = 10000, int maxSteps = 500, int idleTicks = 3)
        {
            await Drive(work, realTimeoutMs, maxSteps, idleTicks);
            return await work;
        }

        /// <summary>As <see cref="RunAsync{T}"/> for a task with no result.</summary>
        public async Task RunAsync(Task work, int realTimeoutMs = 10000, int maxSteps = 500, int idleTicks = 3)
        {
            await Drive(work, realTimeoutMs, maxSteps, idleTicks);
            await work;
        }

        /// <summary>Waits real time until <paramref name="condition"/> holds; fails after <paramref name="realTimeoutMs"/>.</summary>
        public static async Task Until(Func<bool> condition, string what, int realTimeoutMs = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > realTimeoutMs)
                {
                    Assert.Fail("timed out waiting for: " + what);
                }

                await Task.Delay(2);
            }
        }

        private async Task Drive(Task work, int realTimeoutMs, int maxSteps, int idleTicks)
        {
            var clock = Stopwatch.StartNew();
            int steps = 0;
            int idle = 0;
            while (!work.IsCompleted)
            {
                if (clock.ElapsedMilliseconds > realTimeoutMs)
                {
                    Assert.Fail($"the work did not finish within {realTimeoutMs} ms of real time ({steps} virtual jumps, {PendingCount} pending)");
                }

                await Task.Delay(2);
                if (work.IsCompleted)
                {
                    break;
                }

                // Only jump when nothing moved for a few polls, so a continuation that is about to
                // register its next delay (or cancel this one) runs first.
                idle++;
                if (idle < idleTicks || PendingCount == 0)
                {
                    continue;
                }

                idle = 0;
                if (++steps > maxSteps)
                {
                    Assert.Fail($"more than {maxSteps} virtual jumps; the code under test loops");
                }

                AdvanceToNext();
            }
        }

        private sealed class Pending
        {
            public Pending(TaskCompletionSource<bool> completion)
            {
                Completion = completion;
            }

            public TaskCompletionSource<bool> Completion { get; }

            public DateTimeOffset Due { get; set; }

            public CancellationTokenRegistration Registration { get; set; }
        }
    }
}
