using System;

namespace PingCore.Fleet.Sessions
{
    /// <summary>What the <see cref="JoinableSessionKeeper"/> does next.</summary>
    public enum JoinableStep
    {
        /// <summary>Nothing until the seats change or the next republish is due.</summary>
        None = 0,

        /// <summary><c>POST /v1/sessions/{allocationId}/joinable</c> with the open seats.</summary>
        Publish = 1,

        /// <summary><c>DELETE /v1/sessions/{allocationId}/joinable</c>: no open seats.</summary>
        Withdraw = 2,
    }

    /// <summary>What the keeper knows about its record, for <see cref="JoinablePlan.Next"/>.</summary>
    public struct JoinableState
    {
        /// <summary>The open seats of the last publish the supervisor accepted (2xx), or null when none is live.</summary>
        public int? LiveSeats;

        /// <summary>When that publish was accepted.</summary>
        public DateTimeOffset? LastPublishAt;

        /// <summary>A record may be stored even though no 2xx confirmed it (a publish whose answer was lost); a withdraw is then owed when seats reach 0.</summary>
        public bool MaybeLive;

        /// <summary>When the last call failed, or null after a success. The next try waits <see cref="JoinablePlan.RetryDelay"/>.</summary>
        public DateTimeOffset? LastFailureAt;
    }

    /// <summary>
    /// The joinable record policy, pure. Open seats are
    /// <c>max(0, min(maxPlayers - connected - expectedJoiners, playersCapacity - playersCount))</c>, clamped to
    /// the <c>players</c> counter's free capacity so a publish never asks for more seats than Discovery
    /// accepts; publish when they change and again every <c>ttlSeconds / 2</c>; 0 means withdraw.
    /// </summary>
    public static class JoinablePlan
    {
        /// <summary>How long to wait after a failed publish or withdraw before trying again.</summary>
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

        /// <summary>The open seats to publish. A <c>players</c> counter that is absent (either value null) does not clamp.</summary>
        public static int OpenSeats(int maxPlayers, int connected, int expectedJoiners, long? playersCapacity, long? playersCount)
        {
            long seats = (long)maxPlayers - Math.Max(0, connected) - Math.Max(0, expectedJoiners);
            if (playersCapacity.HasValue && playersCount.HasValue)
            {
                seats = Math.Min(seats, playersCapacity.Value - playersCount.Value);
            }

            return (int)Math.Max(0, Math.Min(int.MaxValue, seats));
        }

        /// <summary>The republish interval: half the record's lifetime.</summary>
        public static TimeSpan RepublishInterval(int ttlSeconds) => TimeSpan.FromSeconds(ttlSeconds / 2.0);

        /// <summary>The next step for <paramref name="openSeats"/> given <paramref name="state"/> at <paramref name="now"/>.</summary>
        public static JoinableStep Next(int openSeats, JoinableState state, int ttlSeconds, DateTimeOffset now)
        {
            if (state.LastFailureAt.HasValue && now - state.LastFailureAt.Value < RetryDelay)
            {
                return JoinableStep.None;
            }

            if (openSeats <= 0)
            {
                return state.LiveSeats.HasValue || state.MaybeLive ? JoinableStep.Withdraw : JoinableStep.None;
            }

            if (!state.LiveSeats.HasValue || state.LiveSeats.Value != openSeats || !state.LastPublishAt.HasValue)
            {
                return JoinableStep.Publish;
            }

            return now - state.LastPublishAt.Value >= RepublishInterval(ttlSeconds) ? JoinableStep.Publish : JoinableStep.None;
        }

        /// <summary>How long until <see cref="Next"/> can change without new input: the retry or the republish; null when only new seats can.</summary>
        public static TimeSpan? NextDue(JoinableState state, int ttlSeconds, DateTimeOffset now)
        {
            if (state.LastFailureAt.HasValue)
            {
                TimeSpan retry = state.LastFailureAt.Value + RetryDelay - now;
                return retry > TimeSpan.Zero ? retry : TimeSpan.Zero;
            }

            if (state.LiveSeats.HasValue && state.LastPublishAt.HasValue)
            {
                TimeSpan republish = state.LastPublishAt.Value + RepublishInterval(ttlSeconds) - now;
                return republish > TimeSpan.Zero ? republish : TimeSpan.Zero;
            }

            return null;
        }
    }
}
