using System;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The timing glue of the WebGL beacon transport (<c>WebGlEchoTransport</c>, compiled only for WebGL players), pure and
    /// compiled everywhere so the editor tests reach it. <c>PingCoreLatency.jslib</c> passes each message's age with it
    /// (<c>performance.now()</c> just before the call minus the message event's <c>timeStamp</c>, when the browser created
    /// the event); the transport turns that into the <see cref="System.Diagnostics.Stopwatch"/> timestamp of the arrival,
    /// the clock the probe read just before its send. The plug-in's header says exactly which waits that age covers.
    /// </summary>
    internal static class WebGlEchoClock
    {
        /// <summary>
        /// The <see cref="System.Diagnostics.Stopwatch"/> timestamp at which a message arrived: <paramref name="nowTimestamp"/>
        /// (read when C# got the message) minus its age in microseconds, in ticks of <paramref name="frequency"/> per second.
        /// A negative age counts as 0; the result never goes below 0.
        /// </summary>
        public static long ArrivalTimestamp(long nowTimestamp, int ageMicros, long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency), "the clock frequency must be positive");
            }

            if (ageMicros <= 0)
            {
                return nowTimestamp;
            }

            // Integer arithmetic, exact, and free of overflow for any clock below 4e15 ticks per second: the whole ticks per
            // microsecond, then the remainder rounded half away from zero.
            long perMicro = frequency / 1_000_000;
            long remainder = frequency % 1_000_000;
            long ageTicks = (ageMicros * perMicro) + (((ageMicros * remainder) + 500_000) / 1_000_000);
            return ageTicks >= nowTimestamp ? 0 : nowTimestamp - ageTicks;
        }
    }
}
