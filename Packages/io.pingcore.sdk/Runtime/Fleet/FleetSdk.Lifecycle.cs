using System;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Fleet
{
    /// <summary>The lifecycle calls: start, ready, shutdown and the process stopping.</summary>
    public sealed partial class FleetSdk
    {
        /// <summary>How many times <see cref="StartAsync"/> reads <c>GET /gameserver</c> before it gives up.</summary>
        internal const int StartAttempts = 30;

        /// <summary>The pause between <see cref="StartAsync"/> reads.</summary>
        internal static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(1);

        private Task<bool> startTask;
        private bool startSucceeded;

        /// <inheritdoc />
        public Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            if (!IsHosted)
            {
                return Task.FromResult(false);
            }

            TaskCompletionSource<bool> completion;
            FleetStateChange transition;
            lock (gate)
            {
                if (disposed || state == FleetState.Stopping)
                {
                    return Task.FromResult(false);
                }

                // One start at a time; a finished successful start stays started, and a failed
                // one (Unreachable, or cancelled) may be retried.
                if (startTask != null && (!startTask.IsCompleted || startSucceeded))
                {
                    return startTask;
                }

                completion = new TaskCompletionSource<bool>();
                startTask = completion.Task;
                transition = TransitionLocked(FleetInput.Of(FleetInputKind.Start), "start");
            }

            // Outside the lock: a handler, or a transport that completes synchronously, never
            // runs while the shim holds it.
            if (transition != null)
            {
                Raise(StateChanged, transition, nameof(StateChanged));
            }

            CompleteStart(completion, cancellationToken);
            return completion.Task;
        }

        private async void CompleteStart(TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
        {
            bool started = false;
            try
            {
                started = await RunStartAsync(cancellationToken);
            }
            catch (Exception e)
            {
                Log(FleetLogLevel.Error, "start", "start failed with " + e.GetType().Name + ": " + e.Message, 0, null);
            }
            finally
            {
                completion.TrySetResult(started);
            }
        }

        /// <inheritdoc />
        public async Task<FleetCallResult> ReadyAsync(CancellationToken cancellationToken)
        {
            if (!IsHosted)
            {
                return InertResult();
            }

            // The supervisor writes the Ready frame before it answers, so the frame may already
            // have moved the state to Ready; ReadyAccepted is then no transition.
            LocalSdkAnswer answer = await caller.SendAsync("POST", "/ready", "{}", cancellationToken);
            LogAnswer("ready", answer, true);
            if (answer.Outcome == FleetCallOutcome.Ok)
            {
                Transition(FleetInput.Of(FleetInputKind.ReadyAccepted), "ready");
                StartHealthPings();
            }

            return answer.ToResult();
        }

        /// <inheritdoc />
        public async Task<FleetCallResult> ShutdownAsync(CancellationToken cancellationToken)
        {
            if (!IsHosted)
            {
                return InertResult();
            }

            LocalSdkAnswer answer = await caller.SendAsync("POST", "/shutdown", "{}", cancellationToken);
            LogAnswer("shutdown", answer, true);
            if (answer.Outcome == FleetCallOutcome.Ok)
            {
                Transition(FleetInput.Of(FleetInputKind.ShutdownAccepted), "shutdown");
            }

            return answer.ToResult();
        }

        /// <inheritdoc />
        public void NotifyProcessStopping()
        {
            if (!IsHosted)
            {
                return;
            }

            lock (gate)
            {
                if (state == FleetState.Stopping || disposed)
                {
                    return;
                }

                stoppingAt = scheduler.UtcNow;
            }

            Transition(FleetInput.Of(FleetInputKind.ProcessStopping), "processStopping");
            StopWatch();
            Log(FleetLogLevel.Info, "stopping", "the process is stopping: the watch and health pings are stopped, and a refused call from here on is expected (endpointClosed)", 0, null);
        }

        private async Task<bool> RunStartAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= StartAttempts; attempt++)
            {
                LocalSdkAnswer answer = await caller.SendAsync("GET", "/gameserver", null, cancellationToken);
                if (answer.Outcome == FleetCallOutcome.Cancelled)
                {
                    return false;
                }

                if (answer.Outcome == FleetCallOutcome.Ok)
                {
                    Wire.GameServerView view = LocalSdkValues.TryDeserialize<Wire.GameServerView>(answer.Body, out string problem);
                    if (view != null)
                    {
                        lock (gate)
                        {
                            if (state == FleetState.Stopping || disposed)
                            {
                                return false;
                            }

                            startSucceeded = true;
                        }

                        ApplyView(new GameServerSnapshot(view), FleetInputKind.FirstView, "start");
                        Log(FleetLogLevel.Info, "gameserver", "read the GameServer view (attempt " + attempt + "); opening the watch stream", answer.Status, FleetCallOutcome.Ok);
                        StartWatch();
                        return true;
                    }

                    Log(FleetLogLevel.Warning, "gameserver", "GET /gameserver answered a body that is not a GameServer view (" + problem + ")", answer.Status, answer.Outcome);
                }
                else if (attempt == 1 || attempt % 10 == 0)
                {
                    Log(FleetLogLevel.Warning, "gameserver", "GET /gameserver attempt " + attempt + " of " + StartAttempts + " failed: " + answer.Message, answer.Status, answer.Outcome);
                }

                if (attempt == StartAttempts)
                {
                    break;
                }

                try
                {
                    await scheduler.DelayAsync(StartRetryInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            Transition(FleetInput.Of(FleetInputKind.StartFailed), "start");
            Log(FleetLogLevel.Error, "gameserver", "the local SDK endpoint did not serve the GameServer view after " + StartAttempts + " attempts", 0, FleetCallOutcome.Unreachable);
            return false;
        }
    }
}
