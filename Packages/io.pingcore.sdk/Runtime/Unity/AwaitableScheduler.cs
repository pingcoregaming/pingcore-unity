using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using UnityEngine;

namespace PingCore.Unity
{
    /// <summary>
    /// <see cref="IScheduler"/> on <c>Awaitable.WaitForSecondsAsync</c>: delays never block a thread
    /// or start a timer, and continuations stay on the Unity main thread. A zero or negative delay
    /// waits one frame. Use it from the main thread. The clock is <see cref="DateTimeOffset.UtcNow"/>.
    /// (The local SDK shim keeps its own internal scheduler, so <c>PingCore.Fleet</c> does not
    /// depend on this assembly.)
    /// </summary>
    public sealed class AwaitableScheduler : IScheduler
    {
        /// <inheritdoc />
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        /// <inheritdoc />
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
}
