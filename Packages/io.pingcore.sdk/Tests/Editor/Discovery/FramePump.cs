using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// A deliberately slow main thread: a <see cref="SynchronizationContext"/> that runs posted
    /// continuations only once per frame, like Unity's. <see cref="Run{T}"/> installs it on the test
    /// thread, starts the work there and pumps a frame every <see cref="FrameMs"/>, so every
    /// <c>await</c> in the code under test resumes at the next frame boundary. A continuation posted
    /// while a frame runs waits for the next frame, as on Unity. Frame boundaries are spun on a
    /// <see cref="Stopwatch"/> (<c>Thread.Sleep</c> rounds to 15.6 ms on Windows).
    /// </summary>
    internal sealed class FramePump : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> posted = new ConcurrentQueue<(SendOrPostCallback, object)>();

        public FramePump(double frameMs)
        {
            FrameMs = frameMs;
        }

        public double FrameMs { get; }

        /// <summary>Frames pumped by the last <see cref="Run{T}"/>.</summary>
        public int Frames { get; private set; }

        public override void Post(SendOrPostCallback d, object state) => posted.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object state) => throw new NotSupportedException("the frame pump only posts");

        public override SynchronizationContext CreateCopy() => this;

        public T Run<T>(Func<Task<T>> start, int realTimeoutMs = 20000)
        {
            SynchronizationContext previous = Current;
            SetSynchronizationContext(this);
            try
            {
                Frames = 0;
                var clock = Stopwatch.StartNew();
                Task<T> work = start();
                double next = FrameMs;
                while (!work.IsCompleted)
                {
                    if (clock.ElapsedMilliseconds > realTimeoutMs)
                    {
                        Assert.Fail($"the work did not finish within {realTimeoutMs} ms ({Frames} frames)");
                    }

                    while (clock.Elapsed.TotalMilliseconds < next)
                    {
                        Thread.SpinWait(20);
                    }

                    RunFrame();
                    next += FrameMs;
                    if (clock.Elapsed.TotalMilliseconds > next)
                    {
                        // A frame overran: the next one is a whole frame later, never sooner.
                        next = clock.Elapsed.TotalMilliseconds + FrameMs;
                    }
                }

                return work.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        private void RunFrame()
        {
            Frames++;
            int due = posted.Count;
            for (int i = 0; i < due && posted.TryDequeue(out (SendOrPostCallback Callback, object State) item); i++)
            {
                item.Callback(item.State);
            }
        }
    }
}
