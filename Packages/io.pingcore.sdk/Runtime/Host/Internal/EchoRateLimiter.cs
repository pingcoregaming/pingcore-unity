using System;
using System.Collections.Generic;

namespace PingCore.Discovery.Host
{
    /// <summary>
    /// The echo responder's per-source cap, pure. A reply is the same size as the challenge, so the
    /// echo amplifies nothing, but a spoofed source could still use it to reflect traffic at a third
    /// party; so each source address gets at most <see cref="PerSourceLimit"/> replies per
    /// <see cref="Window"/>, and all sources together at most <see cref="GlobalLimit"/>. Discovery
    /// probes once on a game server's first heartbeat and then on a re-probe cadence of minutes, so
    /// a real prober never comes near either cap. At most <see cref="MaxSources"/> sources are
    /// tracked; when the table is full and no window has expired, a new source is refused.
    /// </summary>
    internal sealed class EchoRateLimiter
    {
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        public const int PerSourceLimit = 5;
        public const int GlobalLimit = 100;
        public const int MaxSources = 256;

        private readonly Dictionary<string, Bucket> sources = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly int perSourceLimit;
        private readonly int globalLimit;
        private DateTimeOffset globalStart;
        private int globalCount;

        public EchoRateLimiter()
            : this(PerSourceLimit, GlobalLimit)
        {
        }

        public EchoRateLimiter(int perSourceLimit, int globalLimit)
        {
            this.perSourceLimit = Math.Max(1, perSourceLimit);
            this.globalLimit = Math.Max(1, globalLimit);
            globalStart = DateTimeOffset.MinValue;
        }

        /// <summary>Records one challenge from <paramref name="source"/> at <paramref name="now"/>; true when it may be answered.</summary>
        public bool TryAcquire(string source, DateTimeOffset now)
        {
            if (source == null)
            {
                return false;
            }

            if (now - globalStart >= Window)
            {
                globalStart = now;
                globalCount = 0;
            }

            if (!sources.TryGetValue(source, out Bucket bucket))
            {
                if (sources.Count >= MaxSources && !EvictExpired(now))
                {
                    return false;
                }

                bucket = new Bucket { Start = now, Count = 0 };
                sources[source] = bucket;
            }
            else if (now - bucket.Start >= Window)
            {
                bucket.Start = now;
                bucket.Count = 0;
            }

            if (bucket.Count >= perSourceLimit || globalCount >= globalLimit)
            {
                return false;
            }

            bucket.Count++;
            globalCount++;
            return true;
        }

        /// <summary>Sources currently tracked.</summary>
        public int TrackedSources => sources.Count;

        private bool EvictExpired(DateTimeOffset now)
        {
            var expired = new List<string>();
            foreach (KeyValuePair<string, Bucket> entry in sources)
            {
                if (now - entry.Value.Start >= Window)
                {
                    expired.Add(entry.Key);
                }
            }

            foreach (string key in expired)
            {
                sources.Remove(key);
            }

            return expired.Count > 0;
        }

        private sealed class Bucket
        {
            public DateTimeOffset Start;
            public int Count;
        }
    }
}
