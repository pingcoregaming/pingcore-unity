using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>
    /// The watch stream reader. An established stream that closes opens the <see cref="EndpointClosedGrace"/>
    /// window; on a real container stop the supervisor keeps the stream open until SIGTERM, so the
    /// refused-after-answered rule (<see cref="TransportFailure"/>) is what classifies those calls.
    /// </summary>
    public sealed partial class FleetSdk
    {
        /// <summary>First reconnect delay; doubles per failure up to <see cref="WatchBackoffMax"/>, back to this after a line arrives.</summary>
        internal static readonly TimeSpan WatchBackoffInitial = TimeSpan.FromSeconds(1);

        /// <summary>Largest reconnect delay.</summary>
        internal static readonly TimeSpan WatchBackoffMax = TimeSpan.FromSeconds(10);

        /// <summary>Failed reconnects in a row, from Ready or InSession, before the state is Unreachable.</summary>
        internal const int WatchLostAfter = 3;

        private CancellationTokenSource watchCts;
        private bool watchConnected;
        private DateTimeOffset? watchClosedAt;

        private void StartWatch()
        {
            CancellationToken token;
            lock (gate)
            {
                if (watchCts != null || disposed || state == FleetState.Stopping)
                {
                    return;
                }

                watchCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                token = watchCts.Token;
            }

            RunWatch(token);
        }

        private void StopWatch()
        {
            lock (gate)
            {
                watchCts?.Cancel();
                watchConnected = false;
                StopHealthPingsLocked();
            }
        }

        private async void RunWatch(CancellationToken token)
        {
            try
            {
                await WatchLoopAsync(token);
            }
            catch (Exception e)
            {
                Log(FleetLogLevel.Error, "watch", "the watch loop stopped with " + e.GetType().Name + ": " + e.Message, 0, null);
            }
        }

        private async Task WatchLoopAsync(CancellationToken token)
        {
            string url = caller.BaseUrl + "/watch/gameserver";
            TimeSpan backoff = WatchBackoffInitial;
            int failedInARow = 0;
            bool everConnected = false;
            while (!token.IsCancellationRequested)
            {
                bool gotLine = false;
                string ended;
                try
                {
                    LineStreamResult result = await lineStream.ReadLinesAsync(url, line =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!gotLine)
                        {
                            gotLine = true;
                            backoff = WatchBackoffInitial;
                            if (everConnected)
                            {
                                Log(FleetLogLevel.Info, "watch", "watch stream reconnected; the first frame re-synchronises the state", 0, null);
                            }

                            everConnected = true;
                            failedInARow = 0;
                            caller.MarkAnswered();
                            lock (gate)
                            {
                                watchConnected = true;
                            }
                        }

                        OnWatchLine(line);
                    }, token);
                    ended = result.End == LineStreamEnd.NotSuccess ? "the endpoint answered HTTP " + result.Status
                        : result.End == LineStreamEnd.LineTooLong ? "a line exceeded " + (WatchLineSplitter.DefaultMaxLineBytes / (1024 * 1024)) + " MiB"
                        : "the endpoint closed the stream";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    ended = "transport failure (" + e.GetType().Name + ")";
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                bool lost = false;
                lock (gate)
                {
                    watchConnected = false;

                    // Only an established stream closing marks the endpoint going away; a failed
                    // reconnect does not extend the EndpointClosed window.
                    if (gotLine)
                    {
                        watchClosedAt = scheduler.UtcNow;
                    }
                }

                if (!gotLine)
                {
                    failedInARow++;
                    lost = failedInARow == WatchLostAfter;
                }

                Log(FleetLogLevel.Warning, "watch", (gotLine ? "watch stream closed: " : "watch reconnect " + failedInARow + " failed: ") + ended + "; retrying in " + backoff.TotalSeconds + " s", 0, null);
                if (lost)
                {
                    Transition(FleetInput.Of(FleetInputKind.WatchLost), "watchLost");
                }

                try
                {
                    await scheduler.DelayAsync(backoff, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, WatchBackoffMax.Ticks));
            }
        }

        private void OnWatchLine(string line)
        {
            WatchFrame frame = LocalSdkValues.TryDeserialize<WatchFrame>(line, out string problem);
            if (frame?.Result == null)
            {
                Log(FleetLogLevel.Warning, "watch", "skipped an undecodable watch line (" + line.Length + " characters, " + (problem ?? "no result") + ")", 0, null);
                return;
            }

            ApplyView(new GameServerSnapshot(frame.Result), FleetInputKind.Frame, "watch");
        }
    }
}
