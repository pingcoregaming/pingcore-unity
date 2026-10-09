using System;
using PingCore.Core;

namespace PingCore.Fleet
{
    /// <summary>
    /// Options for <see cref="FleetSdk.Create"/>. Every member has a working default; tests
    /// replace the seams (environment, transports, scheduler, log).
    /// </summary>
    public sealed class FleetSdkOptions
    {
        /// <summary>
        /// Reads an environment variable. The shim reads <c>AGONES_SDK_HTTP_PORT</c> and nothing
        /// else. Default <see cref="Environment.GetEnvironmentVariable(string)"/>.
        /// </summary>
        public Func<string, string> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

        /// <summary>Unary HTTP calls. Default: the internal loopback transport (<c>HttpClient</c>, no proxy, <see cref="CallTimeout"/>).</summary>
        public IHttpTransport Transport { get; set; }

        /// <summary>The watch stream. Default: the same loopback transport, with no timeout.</summary>
        public ILineStreamTransport LineStream { get; set; }

        /// <summary>Every delay and the clock. Default: a scheduler on <c>Awaitable.WaitForSecondsAsync</c>, which must be used from the Unity main thread.</summary>
        public IScheduler Scheduler { get; set; }

        /// <summary>Interval of the <c>POST /health</c> pings that start after a 2xx <c>/ready</c>. <see cref="TimeSpan.Zero"/> disables them. Default 2 s.</summary>
        public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Timeout of one unary call on the default transport. Default 5 s.</summary>
        public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long <see cref="IFleetSdk.GetReservationAsync"/> keeps asking while the endpoint
        /// answers <c>reservation not found</c>: the push can trail the player, and the store is
        /// empty for a moment after the supervisor's agent reconnects. The last lookup is made at
        /// the end of this window, never before it. Default 2 s.
        /// </summary>
        public TimeSpan ReservationWait { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Interval between reservation lookups inside <see cref="ReservationWait"/>; the last interval is shortened to end at the window's end. Default 250 ms.</summary>
        public TimeSpan ReservationPoll { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How long <see cref="IFleetSdk.AllocateSelfAsync"/> waits, after a 2xx, for the watch stream to carry the
        /// self-allocation. The supervisor writes that frame before it answers, so it is normally there at once.
        /// Default 2 s.
        /// </summary>
        public TimeSpan SelfAllocationWait { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long <see cref="IFleetSdk.AllocateSelfAsync"/> sends no second <c>POST /allocate</c> after one whose answer was
        /// lost (<see cref="FleetCallOutcome.Unreachable"/>) or whose frame did not come within <see cref="SelfAllocationWait"/>:
        /// the supervisor may have made that self-allocation, and a second one would replace it. A frame carrying any
        /// allocation ends the wait early. Default 10 s.
        /// </summary>
        public TimeSpan SelfAllocationGrace { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Receives the shim's diagnostics. Entries never carry request headers or bodies.
        /// Default: the Unity console (<c>Debug.Log</c>, <c>LogWarning</c>, <c>LogError</c>). Pass
        /// <c>_ =&gt; { }</c> to silence it.
        /// </summary>
        public Action<FleetLogEntry> Log { get; set; }
    }
}
