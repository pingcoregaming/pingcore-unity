using System;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Fleet
{
    /// <summary>
    /// One operation in flight at a time, shared: a caller that arrives while one runs joins it instead of
    /// starting another; the next caller after it ends starts a fresh one. A caller's cancellation stops
    /// only that caller's wait (an <see cref="OperationCanceledException"/>), never the shared operation.
    /// </summary>
    /// <typeparam name="T">The operation's result.</typeparam>
    internal sealed class SingleFlight<T>
    {
        private readonly object gate = new object();
        private readonly Func<Task<T>> operation;
        private readonly Func<Exception, T> onFault;
        private Task<T> inflight;

        /// <param name="operation">Starts the shared operation.</param>
        /// <param name="onFault">The result when the operation throws, so a joiner never sees the exception.</param>
        public SingleFlight(Func<Task<T>> operation, Func<Exception, T> onFault)
        {
            this.operation = operation ?? throw new ArgumentNullException(nameof(operation));
            this.onFault = onFault ?? throw new ArgumentNullException(nameof(onFault));
        }

        /// <summary>Joins the operation in flight, or starts one, and waits for it.</summary>
        public async Task<T> RunAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<T> flight = null;
            Task<T> shared;
            lock (gate)
            {
                if (inflight == null)
                {
                    flight = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    inflight = flight.Task;
                }

                shared = inflight;
            }

            if (flight != null)
            {
                Start(flight);
            }

            if (!cancellationToken.CanBeCanceled || shared.IsCompleted)
            {
                return await shared;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(shared, cancelled.Task) == shared)
                {
                    return await shared;
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        private async void Start(TaskCompletionSource<T> flight)
        {
            T result;
            try
            {
                result = await operation();
            }
            catch (Exception e)
            {
                result = onFault(e);
            }

            lock (gate)
            {
                inflight = null;
            }

            flight.TrySetResult(result);
        }
    }
}
