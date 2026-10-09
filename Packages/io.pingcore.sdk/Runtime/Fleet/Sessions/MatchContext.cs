using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PingCore.Core.Handshake;

namespace PingCore.Fleet.Sessions
{
    /// <summary>
    /// The allocation context a matchmaker allocation carries, typed:
    /// <c>{matchmaker, queue, sessionSize, players, relaxed, backfill, location, roster}</c>. A match
    /// context has no <c>sessionId</c>: the allocation id is the session. An allocation a backend made
    /// directly carries whatever context that backend sent; it parses with
    /// <see cref="IsMatchmaker"/> false, an empty roster and <see cref="HasRoster"/> false, and
    /// <see cref="Raw"/> keeps it all.
    /// </summary>
    public sealed class MatchContext
    {
        private MatchContext(string allocationId, JObject raw, bool contextInvalid)
        {
            AllocationId = allocationId;
            Raw = raw ?? new JObject();
            ContextInvalid = contextInvalid;
            IsMatchmaker = ContextValues.Bool(Raw, "matchmaker");
            Queue = ContextValues.String(Raw, "queue");
            SessionSize = ContextValues.Int(Raw, "sessionSize");
            Players = ContextValues.Int(Raw, "players");
            Relaxed = ContextValues.Bool(Raw, "relaxed");
            Backfill = ContextValues.Bool(Raw, "backfill");
            Location = ContextValues.String(Raw, "location");
            JToken roster = Raw["roster"];
            Roster = RosterEntry.ParseRoster(roster);
            HasRoster = contextInvalid || IsMatchmaker || !(roster == null || roster.Type == JTokenType.Null || (roster is JArray list && list.Count == 0));
            int total = 0;
            foreach (RosterEntry entry in Roster)
            {
                total += entry.PartySize;
            }

            RosterPlayers = total;
        }

        /// <summary>The allocation id: the session id for <see cref="IFleetSdk.EndSessionAsync"/> and the <c>allocationId</c> a <c>match</c> join ticket names.</summary>
        public string AllocationId { get; }

        /// <summary><c>matchmaker</c>: true when Discovery's matchmaker made this allocation.</summary>
        public bool IsMatchmaker { get; }

        /// <summary><c>queue</c>, or null.</summary>
        public string Queue { get; }

        /// <summary><c>sessionSize</c>: the size the match was formed for, or null.</summary>
        public int? SessionSize { get; }

        /// <summary><c>players</c>: the players the match holds (the sum of the roster's party sizes), or null.</summary>
        public int? Players { get; }

        /// <summary><c>relaxed</c>: the match formed below <c>sessionSize</c> after the relax delay.</summary>
        public bool Relaxed { get; }

        /// <summary><c>backfill</c>: always false for a formed match; a backfill arrives through <see cref="BackfillWatcher"/>.</summary>
        public bool Backfill { get; }

        /// <summary><c>location</c>: the placement location, or null.</summary>
        public string Location { get; }

        /// <summary><c>roster</c>: one entry per ticket; entries that can admit no one are skipped. Never null.</summary>
        public IReadOnlyList<RosterEntry> Roster { get; }

        /// <summary>
        /// False when the allocation carries no roster: no <c>roster</c> key, or a null or empty one, on an
        /// allocation the matchmaker did not make (a backend allocation, or a supervisor self-allocation
        /// <c>self-&lt;ms&gt;</c>). A <c>match</c> join for such an allocation is admitted on its
        /// <c>allocationId</c> alone. A roster that is present but has no usable entry, or any matchmaker
        /// allocation, stays true, so it fails closed as <c>not_in_roster</c>. So does a context annotation
        /// that did not parse (<see cref="ContextInvalid"/>): an unreadable context is never rosterless.
        /// </summary>
        public bool HasRoster { get; }

        /// <summary>The context annotation was present but did not parse as a JSON object (<see cref="AllocationInfo.ContextInvalid"/>).</summary>
        public bool ContextInvalid { get; }

        /// <summary>The sum of the roster's party sizes: how many players to wait for.</summary>
        public int RosterPlayers { get; }

        /// <summary>The whole context object, for the game's own keys. Never null.</summary>
        public JObject Raw { get; }

        /// <summary>Parses the context of an allocation; null when <paramref name="allocation"/> is null. Never throws.</summary>
        public static MatchContext Parse(AllocationInfo allocation)
        {
            return allocation == null ? null : new MatchContext(allocation.AllocationId, allocation.Context, allocation.ContextInvalid);
        }

        /// <summary>Parses a context object for <paramref name="allocationId"/>. Never throws.</summary>
        public static MatchContext Parse(string allocationId, JObject context) => new MatchContext(allocationId, context, false);
    }
}
