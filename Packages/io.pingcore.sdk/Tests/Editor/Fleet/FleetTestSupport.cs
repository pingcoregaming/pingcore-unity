using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// A virtual clock for the shim: each delay advances <see cref="UtcNow"/> by its full length
    /// but waits only a scaled real time (1/100, between 1 and 50 ms), so a 2 s health interval
    /// or a 10 s reconnect backoff takes milliseconds. Concurrent delays move the clock to the
    /// latest target, never backwards. Every requested delay is recorded.
    /// With <c>holdDelays</c> no real time passes at all: each delay waits until the test takes it
    /// (<see cref="NextHeldDelayAsync"/>, in request order) and releases it (<see cref="HeldDelay.Release"/>),
    /// which moves the clock; a cancelled delay is cancelled at once.
    /// </summary>
    internal sealed class TestScheduler : IScheduler
    {
        private readonly object gate = new object();
        private readonly List<TimeSpan> delays = new List<TimeSpan>();
        private readonly bool holdDelays;
        private readonly List<HeldDelay> held = new List<HeldDelay>();
        private int nextHeld;
        private TaskCompletionSource<bool> heldRequested;
        private DateTimeOffset now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        public TestScheduler(bool holdDelays = false)
        {
            this.holdDelays = holdDelays;
        }

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

        public void Advance(TimeSpan by)
        {
            lock (gate)
            {
                now += by;
            }
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DateTimeOffset target;
            HeldDelay hold = null;
            TaskCompletionSource<bool> requested = null;
            lock (gate)
            {
                delays.Add(delay);
                target = now + delay;
                if (holdDelays)
                {
                    hold = new HeldDelay(delay);
                    held.Add(hold);
                    requested = heldRequested;
                    heldRequested = null;
                }
            }

            if (hold != null)
            {
                requested?.TrySetResult(true);
                using (cancellationToken.Register(() => hold.Cancel()))
                {
                    await hold.Released;
                }
            }
            else
            {
                int realMs = (int)Math.Max(1, Math.Min(50, delay.TotalMilliseconds / 100));
                await Task.Delay(realMs, cancellationToken);
            }

            lock (gate)
            {
                if (target > now)
                {
                    now = target;
                }
            }
        }

        /// <summary>The next delay the code under test asked for, in request order, still held (hold mode only).</summary>
        public async Task<HeldDelay> NextHeldDelayAsync()
        {
            if (!holdDelays)
            {
                throw new InvalidOperationException("this scheduler does not hold delays");
            }

            while (true)
            {
                Task requested;
                lock (gate)
                {
                    if (nextHeld < held.Count)
                    {
                        return held[nextHeld++];
                    }

                    heldRequested = heldRequested ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    requested = heldRequested.Task;
                }

                await requested;
            }
        }
    }

    /// <summary>A delay a holding <see cref="TestScheduler"/> keeps until the test releases it.</summary>
    internal sealed class HeldDelay
    {
        private readonly TaskCompletionSource<bool> released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldDelay(TimeSpan delay)
        {
            Delay = delay;
        }

        public TimeSpan Delay { get; }

        internal Task Released => released.Task;

        /// <summary>Lets the delay complete; the virtual clock moves by its length.</summary>
        public void Release() => released.TrySetResult(true);

        internal void Cancel() => released.TrySetCanceled();
    }

    /// <summary>Counts an event and lets a test await a count without polling.</summary>
    internal sealed class EventCounter
    {
        private readonly object gate = new object();
        private readonly List<(int Count, TaskCompletionSource<bool> Done)> waiters = new List<(int, TaskCompletionSource<bool>)>();
        private int count;

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return count;
                }
            }
        }

        public void Increment()
        {
            List<TaskCompletionSource<bool>> done;
            lock (gate)
            {
                count++;
                done = waiters.Where(w => w.Count <= count).Select(w => w.Done).ToList();
                waiters.RemoveAll(w => w.Count <= count);
            }

            foreach (TaskCompletionSource<bool> waiter in done)
            {
                waiter.TrySetResult(true);
            }
        }

        /// <summary>Completes once the event has happened <paramref name="atLeast"/> times in all.</summary>
        public Task ReachedAsync(int atLeast)
        {
            lock (gate)
            {
                if (count >= atLeast)
                {
                    return Task.CompletedTask;
                }

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((atLeast, waiter));
                return waiter.Task;
            }
        }
    }

    /// <summary>Collects the shim's log entries.</summary>
    internal sealed class LogCollector
    {
        private readonly object gate = new object();
        private readonly List<FleetLogEntry> entries = new List<FleetLogEntry>();
        private readonly List<(Func<FleetLogEntry, bool> Match, TaskCompletionSource<FleetLogEntry> Done)> waiters = new List<(Func<FleetLogEntry, bool>, TaskCompletionSource<FleetLogEntry>)>();

        public void Add(FleetLogEntry entry)
        {
            List<TaskCompletionSource<FleetLogEntry>> done;
            lock (gate)
            {
                entries.Add(entry);
                done = waiters.Where(w => w.Match(entry)).Select(w => w.Done).ToList();
                waiters.RemoveAll(w => done.Contains(w.Done));
            }

            foreach (TaskCompletionSource<FleetLogEntry> waiter in done)
            {
                waiter.TrySetResult(entry);
            }
        }

        /// <summary>Completes with the first entry, logged already or later, that <paramref name="match"/> accepts.</summary>
        public Task<FleetLogEntry> WaitForAsync(Func<FleetLogEntry, bool> match)
        {
            lock (gate)
            {
                FleetLogEntry logged = entries.FirstOrDefault(match);
                if (logged != null)
                {
                    return Task.FromResult(logged);
                }

                var waiter = new TaskCompletionSource<FleetLogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((match, waiter));
                return waiter.Task;
            }
        }

        public List<FleetLogEntry> Entries
        {
            get
            {
                lock (gate)
                {
                    return entries.ToList();
                }
            }
        }

        public List<FleetLogEntry> For(string call) => Entries.Where(e => e.Call == call).ToList();

        public string Dump() => string.Join("\n", Entries.Select(e => e.ToString()));
    }

    internal static class Wait
    {
        /// <summary>Polls <paramref name="condition"/> in real time until it holds; fails the test after <paramref name="timeoutMs"/>.</summary>
        public static async Task Until(Func<bool> condition, string what, int timeoutMs = 10000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > timeoutMs)
                {
                    Assert.Fail("timed out after " + timeoutMs + " ms waiting for: " + what);
                }

                await Task.Delay(10);
            }
        }

        /// <summary>
        /// Awaits an event-driven <paramref name="task"/>. The timeout is only a guard against a hang, never
        /// part of the test's timing: a test that awaits events through this never polls.
        /// </summary>
        public static async Task<T> Within<T>(Task<T> task, string what, int guardMs = 60000)
        {
            if (await Task.WhenAny(task, Task.Delay(guardMs)) != task)
            {
                Assert.Fail("no " + what + " within the " + guardMs + " ms guard");
            }

            return await task;
        }

        /// <inheritdoc cref="Within{T}(Task{T}, string, int)"/>
        public static async Task Within(Task task, string what, int guardMs = 60000)
        {
            if (await Task.WhenAny(task, Task.Delay(guardMs)) != task)
            {
                Assert.Fail("no " + what + " within the " + guardMs + " ms guard");
            }

            await task;
        }

        /// <summary>Waits real time, for "nothing more happens" checks.</summary>
        public static Task For(int ms) => Task.Delay(ms);
    }
}
