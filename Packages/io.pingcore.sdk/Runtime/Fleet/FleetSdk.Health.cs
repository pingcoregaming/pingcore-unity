using System;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Fleet
{
    /// <summary>The health pings: every <see cref="FleetSdkOptions.HealthInterval"/> after a 2xx Ready, while <see cref="HealthCadence.ShouldPing"/> holds.</summary>
    public sealed partial class FleetSdk
    {
        private CancellationTokenSource healthCts;

        private void StartHealthPings()
        {
            TimeSpan interval = options.HealthInterval;
            if (interval <= TimeSpan.Zero)
            {
                return;
            }

            CancellationToken token;
            lock (gate)
            {
                if (healthCts != null || disposed || !HealthCadence.ShouldPing(state))
                {
                    return;
                }

                healthCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                token = healthCts.Token;
            }

            RunHealth(interval, token);
        }

        private void StopHealthPingsLocked()
        {
            healthCts?.Cancel();
        }

        private async void RunHealth(TimeSpan interval, CancellationToken token)
        {
            try
            {
                await HealthLoopAsync(interval, token);
            }
            catch (Exception e)
            {
                Log(FleetLogLevel.Error, "health", "the health pings stopped with " + e.GetType().Name + ": " + e.Message, 0, null);
            }
        }

        private async Task HealthLoopAsync(TimeSpan interval, CancellationToken token)
        {
            int seq = 0;
            while (true)
            {
                try
                {
                    await scheduler.DelayAsync(interval, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested || !HealthCadence.ShouldPing(State))
                {
                    return;
                }

                seq++;
                LocalSdkAnswer answer = await caller.SendAsync("POST", "/health", "{}", token);
                if (answer.Outcome == FleetCallOutcome.Cancelled)
                {
                    return;
                }

                bool ok = answer.Outcome == FleetCallOutcome.Ok;
                if (HealthCadence.ShouldLog(seq, ok))
                {
                    FleetLogLevel level = ok || answer.Outcome == FleetCallOutcome.EndpointClosed ? FleetLogLevel.Info : FleetLogLevel.Warning;
                    Log(level, "health", "ping " + seq + (ok ? " acknowledged" : " failed: " + answer.Message), answer.Status, answer.Outcome);
                }
            }
        }
    }
}
