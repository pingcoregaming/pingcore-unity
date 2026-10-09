using System;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Core
{
    /// <summary>
    /// Time source and delay for Core. Core never blocks a thread or starts a timer itself:
    /// every wait goes through this interface, so continuations stay on the Unity main thread
    /// (Unity implements it with <c>Awaitable.WaitForSecondsAsync</c>) and WebGL stays possible.
    /// </summary>
    public interface IScheduler
    {
        /// <summary>The current UTC time.</summary>
        DateTimeOffset UtcNow { get; }

        /// <summary>Completes after <paramref name="delay"/>, or is cancelled through <paramref name="cancellationToken"/>.</summary>
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }
}
