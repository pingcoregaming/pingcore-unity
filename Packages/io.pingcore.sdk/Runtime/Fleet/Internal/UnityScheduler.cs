using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using UnityEngine;

namespace PingCore.Fleet
{
    /// <summary>
    /// The default scheduler: <c>Awaitable.WaitForSecondsAsync</c>, so delays never block a
    /// thread or start a timer and continuations stay on the Unity main thread. Use it from
    /// the main thread only.
    /// </summary>
    internal sealed class UnityScheduler : IScheduler
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay <= TimeSpan.Zero)
            {
                await Awaitable.NextFrameAsync(cancellationToken);
                return;
            }

            await Awaitable.WaitForSecondsAsync((float)delay.TotalSeconds, cancellationToken);
        }
    }

    /// <summary>The default log sink: the Unity console.</summary>
    internal static class UnityFleetLog
    {
        public static void Write(FleetLogEntry entry)
        {
            string line = "[PingCore.Fleet] " + entry;
            switch (entry.Level)
            {
                case FleetLogLevel.Error:
                    Debug.LogError(line);
                    break;
                case FleetLogLevel.Warning:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }
        }
    }
}
