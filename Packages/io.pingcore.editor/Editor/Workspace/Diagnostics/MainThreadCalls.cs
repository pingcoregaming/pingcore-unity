using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using UnityEditor;

namespace PingCore.Editor.Workspace.Infrastructure
{
    /// <summary>
    /// Runs short calls on the Editor's main thread for code on another thread, waiting for each at most a bound.
    /// The missing-infrastructure answer runs on a thread-pool thread (the runtime's <c>InfrastructureEditorHook</c>
    /// starts it there so it can never hold Play), but <c>EditorPrefs</c>, <c>SessionState</c> and the credential store
    /// choice are main-thread-only Unity state; this is how those reads reach the main thread. A call made on the main
    /// thread runs inline. A queued call the waiter gave up on is never run.
    /// </summary>
    internal sealed class MainThreadCalls
    {
        private readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        private readonly int mainThreadId;

        /// <param name="mainThreadId">The managed id of the thread that calls <see cref="Drain"/>.</param>
        internal MainThreadCalls(int mainThreadId)
        {
            this.mainThreadId = mainThreadId;
        }

        /// <summary>True on the thread that drains the queue.</summary>
        internal bool OnMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;

        /// <summary>
        /// The Editor's: made on the main thread (an <c>[InitializeOnLoad]</c> constructor) and drained by
        /// <see cref="EditorApplication.update"/>, which also ticks in Play mode. A domain reload makes a new one.
        /// </summary>
        internal static MainThreadCalls ForEditor()
        {
            var calls = new MainThreadCalls(Thread.CurrentThread.ManagedThreadId);
            EditorApplication.update += calls.Drain;
            return calls;
        }

        /// <summary>
        /// Runs <paramref name="call"/> on the main thread and returns its result, or rethrows its exception. Inline on the
        /// main thread; from another thread it waits at most <paramref name="bound"/> for the call to start and as long
        /// again for it to finish, then throws <see cref="TimeoutException"/>.
        /// </summary>
        internal T Run<T>(Func<T> call, TimeSpan bound)
        {
            if (call == null)
            {
                throw new ArgumentNullException(nameof(call));
            }

            if (OnMainThread)
            {
                return call();
            }

            var pending = new Pending<T>(call);
            queue.Enqueue(pending.Execute);
            if (!pending.Done.Wait(bound) && (pending.Abandon() || !pending.Done.Wait(bound)))
            {
                throw new TimeoutException($"The Editor's main thread did not answer within {bound.TotalSeconds:0.#} s.");
            }

            return pending.Result();
        }

        /// <summary>Runs every queued call; the main thread calls it (<see cref="EditorApplication.update"/>, or a test).</summary>
        internal void Drain()
        {
            while (queue.TryDequeue(out Action next))
            {
                next();
            }
        }

        private sealed class Pending<T>
        {
            private const int Queued = 0;
            private const int Running = 1;
            private const int Abandoned = 2;

            private readonly Func<T> call;
            private int state = Queued;
            private T value;
            private ExceptionDispatchInfo error;

            public Pending(Func<T> call)
            {
                this.call = call;
            }

            // Not disposed: the main thread may still set it after the waiter has given up.
            public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);

            public void Execute()
            {
                if (Interlocked.CompareExchange(ref state, Running, Queued) != Queued)
                {
                    return;
                }

                try
                {
                    value = call();
                }
                catch (Exception e)
                {
                    error = ExceptionDispatchInfo.Capture(e);
                }
                finally
                {
                    Done.Set();
                }
            }

            // True when the call had not started, so it never will.
            public bool Abandon() => Interlocked.CompareExchange(ref state, Abandoned, Queued) == Queued;

            public T Result()
            {
                error?.Throw();
                return value;
            }
        }
    }
}
