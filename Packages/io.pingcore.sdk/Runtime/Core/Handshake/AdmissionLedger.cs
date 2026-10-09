using System;
using System.Collections.Generic;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// Who this game server has let in, so one ticket or hold cannot bring in more players than it
    /// paid for. Pure bookkeeping, no I/O. Counts held connections per (<c>allocationId</c>,
    /// <c>ticketId</c>) against the roster's <c>partySize</c>, per <c>reservationId</c> against the hold's
    /// <c>seats</c>, per <c>allocationId</c> for <c>match</c> connections (the (<c>allocationId</c>, <c>*</c>)
    /// count a rosterless allocation is capped by, against <c>maxPlayers</c>), and per <c>playerId</c> (one
    /// live connection each). A seat is held from the accept
    /// (<see cref="Hold"/>) and given back on disconnect or when the approval fails afterwards
    /// (<see cref="Release"/>). A reconnect after a disconnect is fine. Safe to call from any thread;
    /// the approval reads a snapshot and holds in the same main-thread step, so two pending approvals
    /// for one ticket cannot both take its last seat.
    /// </summary>
    public sealed class AdmissionLedger
    {
        private readonly object gate = new object();
        private readonly Dictionary<ulong, Entry> byConnection = new Dictionary<ulong, Entry>();
        private readonly Dictionary<string, int> byTicket = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> byAllocation = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> byReservation = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> byPlayer = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Connections currently held.</summary>
        public int Count
        {
            get
            {
                lock (gate)
                {
                    return byConnection.Count;
                }
            }
        }

        /// <summary>Held connections on (<paramref name="allocationId"/>, <paramref name="ticketId"/>).</summary>
        public int AdmittedForTicket(string allocationId, string ticketId)
        {
            lock (gate)
            {
                return Get(byTicket, TicketKey(allocationId, ticketId));
            }
        }

        /// <summary>Held <c>match</c> connections on <paramref name="allocationId"/>, whatever their ticket id.</summary>
        public int AdmittedForAllocation(string allocationId)
        {
            lock (gate)
            {
                return Get(byAllocation, allocationId);
            }
        }

        /// <summary>Held connections on <paramref name="reservationId"/>.</summary>
        public int AdmittedForReservation(string reservationId)
        {
            lock (gate)
            {
                return Get(byReservation, reservationId);
            }
        }

        /// <summary>True when <paramref name="playerId"/> has a held connection.</summary>
        public bool IsPlayerAdmitted(string playerId)
        {
            lock (gate)
            {
                return Get(byPlayer, playerId) > 0;
            }
        }

        /// <summary>True when <paramref name="connectionId"/> is held.</summary>
        public bool Holds(ulong connectionId)
        {
            lock (gate)
            {
                return byConnection.ContainsKey(connectionId);
            }
        }

        /// <summary>Writes the ledger's counts for <paramref name="ticket"/> into <paramref name="facts"/> (<see cref="AdmissionFacts.AdmittedForTicket"/>, <see cref="AdmissionFacts.AdmittedForAllocation"/>, <see cref="AdmissionFacts.AdmittedForReservation"/>, <see cref="AdmissionFacts.PlayerAlreadyAdmitted"/>).</summary>
        public void Snapshot(JoinTicket ticket, AdmissionFacts facts)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            lock (gate)
            {
                facts.AdmittedForTicket = ticket.TicketId != null ? Get(byTicket, TicketKey(ticket.AllocationId, ticket.TicketId)) : 0;
                facts.AdmittedForAllocation = ticket.Kind == JoinTicketKind.Match ? Get(byAllocation, ticket.AllocationId) : 0;
                facts.AdmittedForReservation = ticket.ReservationId != null ? Get(byReservation, ticket.ReservationId) : 0;
                facts.PlayerAlreadyAdmitted = Get(byPlayer, ticket.PlayerId) > 0;
            }
        }

        /// <summary>
        /// Holds a seat for an admitted connection: its player, its ticket or reservation, and for a
        /// <c>match</c> its allocation. False (and
        /// nothing changes) when <paramref name="connectionId"/> is already held.
        /// </summary>
        public bool Hold(ulong connectionId, JoinTicket ticket)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            var entry = new Entry(
                ticket.TicketId != null ? TicketKey(ticket.AllocationId, ticket.TicketId) : null,
                ticket.Kind == JoinTicketKind.Match ? ticket.AllocationId : null,
                ticket.Kind == JoinTicketKind.Reservation ? ticket.ReservationId : null,
                ticket.PlayerId);
            lock (gate)
            {
                if (byConnection.ContainsKey(connectionId))
                {
                    return false;
                }

                byConnection[connectionId] = entry;
                Add(byTicket, entry.TicketKey, 1);
                Add(byAllocation, entry.AllocationId, 1);
                Add(byReservation, entry.ReservationId, 1);
                Add(byPlayer, entry.PlayerId, 1);
                return true;
            }
        }

        /// <summary>Gives back the seats of <paramref name="connectionId"/>. False when it held none. Idempotent.</summary>
        public bool Release(ulong connectionId)
        {
            lock (gate)
            {
                if (!byConnection.TryGetValue(connectionId, out Entry entry))
                {
                    return false;
                }

                byConnection.Remove(connectionId);
                Add(byTicket, entry.TicketKey, -1);
                Add(byAllocation, entry.AllocationId, -1);
                Add(byReservation, entry.ReservationId, -1);
                Add(byPlayer, entry.PlayerId, -1);
                return true;
            }
        }

        /// <summary>Gives back every seat (for example when the session ends and everyone left).</summary>
        public void Clear()
        {
            lock (gate)
            {
                byConnection.Clear();
                byTicket.Clear();
                byAllocation.Clear();
                byReservation.Clear();
                byPlayer.Clear();
            }
        }

        private static string TicketKey(string allocationId, string ticketId) => (allocationId ?? string.Empty) + "\n" + ticketId;

        private static int Get(Dictionary<string, int> map, string key) => key != null && map.TryGetValue(key, out int n) ? n : 0;

        private static void Add(Dictionary<string, int> map, string key, int delta)
        {
            if (key == null)
            {
                return;
            }

            int next = Get(map, key) + delta;
            if (next <= 0)
            {
                map.Remove(key);
            }
            else
            {
                map[key] = next;
            }
        }

        private readonly struct Entry
        {
            public Entry(string ticketKey, string allocationId, string reservationId, string playerId)
            {
                TicketKey = ticketKey;
                AllocationId = allocationId;
                ReservationId = reservationId;
                PlayerId = playerId;
            }

            public string TicketKey { get; }

            public string AllocationId { get; }

            public string ReservationId { get; }

            public string PlayerId { get; }
        }
    }
}
