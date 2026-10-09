using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PingCore.Core.Handshake;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Sessions
{
    /// <summary>
    /// One backfill, typed: players the matchmaker placed into this game server's running session. Its
    /// context is
    /// <c>{matchmaker, backfill, queue, sessionSize, players, location, sessionId, roster}</c>; the
    /// supervisor's backfill list adds the backfill's own <c>allocationId</c>, the resolved parent
    /// <c>sessionId</c>, <c>claims</c> and <c>deliveredAt</c>. A <c>backfill</c> join ticket names
    /// <see cref="AllocationId"/>.
    /// </summary>
    public sealed class BackfillContext
    {
        private BackfillContext(BackfillView view)
        {
            AllocationId = view.AllocationId;
            Raw = view.Context ?? new JObject();
            SessionId = !string.IsNullOrEmpty(view.SessionId) ? view.SessionId : ContextValues.String(Raw, "sessionId");
            IsMatchmaker = ContextValues.Bool(Raw, "matchmaker");
            Queue = ContextValues.String(Raw, "queue");
            SessionSize = ContextValues.Int(Raw, "sessionSize");
            Players = ContextValues.Int(Raw, "players");
            Location = ContextValues.String(Raw, "location");
            Roster = RosterEntry.ParseRoster(Raw["roster"]);
            DeliveredAt = view.DeliveredAt;
            Claims = view.Claims;
            int total = 0;
            foreach (RosterEntry entry in Roster)
            {
                total += entry.PartySize;
            }

            RosterPlayers = total;
        }

        /// <summary>The backfill's own allocation id: end it alone with <see cref="IFleetSdk.EndSessionAsync"/>; a <c>backfill</c> join ticket names it.</summary>
        public string AllocationId { get; }

        /// <summary>The session the players join: the main allocation id. The supervisor's resolved <c>sessionId</c>, else the context's; null when neither is set.</summary>
        public string SessionId { get; }

        /// <summary><c>matchmaker</c>.</summary>
        public bool IsMatchmaker { get; }

        /// <summary><c>queue</c>, or null.</summary>
        public string Queue { get; }

        /// <summary><c>sessionSize</c>, or null.</summary>
        public int? SessionSize { get; }

        /// <summary><c>players</c>: the seats this backfill takes, or null.</summary>
        public int? Players { get; }

        /// <summary><c>location</c>, or null.</summary>
        public string Location { get; }

        /// <summary><c>roster</c>: the incoming tickets. Never null.</summary>
        public IReadOnlyList<RosterEntry> Roster { get; }

        /// <summary>The sum of the roster's party sizes: the expected joiners until they connect.</summary>
        public int RosterPlayers { get; }

        /// <summary><c>deliveredAt</c>, epoch milliseconds, or null.</summary>
        public long? DeliveredAt { get; }

        /// <summary><c>claims</c>, for example <c>{"players": 2}</c>, or null.</summary>
        public JObject Claims { get; }

        /// <summary>The whole context object. Never null.</summary>
        public JObject Raw { get; }

        /// <summary>Parses one backfill; null when <paramref name="view"/> is null or has no allocation id. Never throws.</summary>
        public static BackfillContext Parse(BackfillView view)
        {
            return view == null || string.IsNullOrEmpty(view.AllocationId) ? null : new BackfillContext(view);
        }
    }
}
