using System;
using PingCore.Discovery.Client;

namespace BeaconRush.Client.Models
{
    /// <summary>Where the Find match screen is.</summary>
    public enum MatchmakingStatus
    {
        /// <summary>Nothing queued.</summary>
        Idle,

        /// <summary>The ticket is being submitted.</summary>
        Submitting,

        /// <summary>The ticket is in the queue.</summary>
        Queued,

        /// <summary>Matched; the client is connecting to the game server.</summary>
        Matched,

        /// <summary>The ticket was refused (by the SDK's floors or by Discovery).</summary>
        Refused,

        /// <summary>The ticket expired in the queue.</summary>
        Expired,

        /// <summary>The player cancelled it.</summary>
        Cancelled,

        /// <summary>Polling failed repeatedly.</summary>
        Failed,
    }

    /// <summary>
    /// The Find match screen, pure: the queue name for this protocol, the status line, the wait timer and
    /// whether Cancel is offered. The flow reports each step; the view only reads.
    /// </summary>
    public sealed class MatchmakingModel
    {
        /// <summary>Players per match the client asks for.</summary>
        public const int SessionSize = 4;

        /// <summary>The smallest match accepted once relaxation is due.</summary>
        public const int MinSessionSize = 2;

        /// <summary>Seconds in the queue before a smaller match may form.</summary>
        public const int RelaxAfterSeconds = 10;

        private DateTimeOffset? queuedAt;
        private DateTimeOffset? endedAt;

        /// <summary>Creates the model for a game with the given network protocol version.</summary>
        public MatchmakingModel(int protocolVersion)
        {
            Queue = QueueFor(protocolVersion);
        }

        /// <summary>The queue for this protocol, <c>rush-p&lt;version&gt;</c>: players of two protocols never meet.</summary>
        public string Queue { get; }

        /// <summary>Where the screen is.</summary>
        public MatchmakingStatus Status { get; private set; } = MatchmakingStatus.Idle;

        /// <summary>The ticket's reference for display and logs (never the ticket id), or null.</summary>
        public string TicketRef { get; private set; }

        /// <summary>The reason of a refusal or failure, or null.</summary>
        public string Reason { get; private set; }

        /// <summary>The match, once matched.</summary>
        public MatchAssignment Match { get; private set; }

        /// <summary>True while the ticket may still be cancelled.</summary>
        public bool CanCancel => Status == MatchmakingStatus.Submitting || Status == MatchmakingStatus.Queued;

        /// <summary>True when a new search may start.</summary>
        public bool CanSearch => Status != MatchmakingStatus.Submitting && Status != MatchmakingStatus.Queued && Status != MatchmakingStatus.Matched;

        /// <summary>The queue name for a protocol version. Pure.</summary>
        public static string QueueFor(int protocolVersion) => "rush-p" + protocolVersion;

        /// <summary>The ticket options this client submits (session 4, relaxing to 2 after 10 s).</summary>
        public TicketOptions CreateTicketOptions(bool joinInProgress)
        {
            return new TicketOptions
            {
                Queue = Queue,
                SessionSize = SessionSize,
                MinSessionSize = MinSessionSize,
                RelaxAfterSeconds = RelaxAfterSeconds,
                JoinInProgress = joinInProgress,
            };
        }

        /// <summary>The player pressed Find match.</summary>
        public void BeginSubmit(DateTimeOffset now)
        {
            Status = MatchmakingStatus.Submitting;
            queuedAt = now;
            endedAt = null;
            TicketRef = null;
            Reason = null;
            Match = null;
        }

        /// <summary>The submit was refused (locally or by Discovery).</summary>
        public void Refuse(string reason, DateTimeOffset now)
        {
            Status = MatchmakingStatus.Refused;
            Reason = string.IsNullOrEmpty(reason) ? "refused" : reason;
            endedAt = now;
        }

        /// <summary>A ticket state arrived from the SDK.</summary>
        public void Apply(TicketState state, string ticketRef, MatchAssignment match, string reason, DateTimeOffset now)
        {
            TicketRef = ticketRef ?? TicketRef;
            switch (state)
            {
                case TicketState.Queued:
                    Status = MatchmakingStatus.Queued;
                    break;
                case TicketState.Matched:
                    Status = MatchmakingStatus.Matched;
                    Match = match;
                    endedAt = now;
                    break;
                case TicketState.Expired:
                    Status = MatchmakingStatus.Expired;
                    endedAt = now;
                    break;
                case TicketState.Cancelled:
                    Status = MatchmakingStatus.Cancelled;
                    endedAt = now;
                    break;
                default:
                    Status = MatchmakingStatus.Failed;
                    Reason = string.IsNullOrEmpty(reason) ? "matchmaking stopped answering" : reason;
                    endedAt = now;
                    break;
            }
        }

        /// <summary>Back to idle (after a match ended or the player left the screen).</summary>
        public void Reset()
        {
            Status = MatchmakingStatus.Idle;
            queuedAt = null;
            endedAt = null;
            TicketRef = null;
            Reason = null;
            Match = null;
        }

        /// <summary>Time in the queue so far (frozen once the ticket ended).</summary>
        public TimeSpan Waited(DateTimeOffset now)
        {
            if (queuedAt == null)
            {
                return TimeSpan.Zero;
            }

            TimeSpan waited = (endedAt ?? now) - queuedAt.Value;
            return waited > TimeSpan.Zero ? waited : TimeSpan.Zero;
        }

        /// <summary>The status line, for example <c>Searching rush-p2... 0:12</c>.</summary>
        public string StatusText(DateTimeOffset now)
        {
            string clock = FormatClock(Waited(now));
            switch (Status)
            {
                case MatchmakingStatus.Idle:
                    return "Press Find match to queue for " + Queue + ".";
                case MatchmakingStatus.Submitting:
                    return "Joining the queue " + Queue + "...";
                case MatchmakingStatus.Queued:
                    return "Searching " + Queue + "... " + clock;
                case MatchmakingStatus.Matched:
                    return "Match found after " + clock + (Match != null && Match.Backfill ? " (joining a match in progress)" : string.Empty) + ". Connecting...";
                case MatchmakingStatus.Refused:
                    return "The queue refused the ticket: " + Reason;
                case MatchmakingStatus.Expired:
                    return "No match was found in time (" + clock + ").";
                case MatchmakingStatus.Cancelled:
                    return "Search cancelled.";
                default:
                    return "Matchmaking failed: " + Reason;
            }
        }

        /// <summary><c>m:ss</c>. Pure.</summary>
        public static string FormatClock(TimeSpan span)
        {
            int seconds = (int)Math.Max(0, Math.Floor(span.TotalSeconds));
            return (seconds / 60) + ":" + (seconds % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
