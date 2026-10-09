using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Handshake;

namespace PingCore.Sdk.Tests.Editor.Handshake
{
    /// <summary>Shared tickets, facts and the contract example files for the handshake tests.</summary>
    internal static class HandshakeTestData
    {
        public const int Protocol = 3;
        public const string Allocation = "alloc-1";
        public const string Backfill = "bf-1";
        public const string TicketId = "q3Vx9mKc0TzR1bN4wYp7Lg";
        public const string PlayerId = "anon:p1";

        public static string ExamplesDirectory => Path.Combine(RepoPaths.ContractsRoot, "handshake", "examples");

        public static string InvalidExamplesDirectory => Path.Combine(ExamplesDirectory, "invalid");

        public static byte[] Example(string name) => File.ReadAllBytes(Path.Combine(ExamplesDirectory, name + ".json"));

        public static JoinTicket Reservation(string playerId = PlayerId, string reservationId = "res-1") =>
            JoinTicket.ForReservation(reservationId, playerId, Protocol);

        public static JoinTicket Match(string playerId = PlayerId, string ticketId = TicketId, string allocationId = Allocation) =>
            JoinTicket.ForMatch(ticketId, allocationId, playerId, Protocol);

        public static JoinTicket BackfillTicket(string playerId = PlayerId, string ticketId = TicketId, string allocationId = Backfill) =>
            JoinTicket.ForBackfill(ticketId, allocationId, playerId, Protocol);

        public static JoinTicket Lan(string playerId = "lan-7f3a2c91") => JoinTicket.ForLan(playerId, Protocol);

        public static AdmissionFacts Facts(HostingMode mode, bool lanOnly = false) => new AdmissionFacts
        {
            ProtocolVersion = Protocol,
            Mode = mode,
            LanOnly = lanOnly,
            EvidenceSource = "test",
        };

        public static IReadOnlyList<RosterEntry> Roster(params RosterEntry[] entries) => entries;

        /// <summary>A hosted game server with an open session whose roster holds the match ticket (party <paramref name="party"/>, submitter <paramref name="rosterPlayer"/>).</summary>
        public static AdmissionFacts HostedSession(int party = 1, string rosterPlayer = PlayerId)
        {
            AdmissionFacts facts = Facts(HostingMode.Hosted);
            facts.CurrentAllocationId = Allocation;
            facts.Roster = Roster(new RosterEntry("other-ticket", 2, "anon:other"), new RosterEntry(TicketId, party, rosterPlayer));
            return facts;
        }

        /// <summary>A hosted game server whose open session is a rosterless backend allocation (set SelfAllocation for a supervisor self-allocation) capped at <paramref name="maxPlayers"/> (0 is no limit).</summary>
        public static AdmissionFacts RosterlessSession(int maxPlayers = 4)
        {
            AdmissionFacts facts = Facts(HostingMode.Hosted);
            facts.CurrentAllocationId = Allocation;
            facts.RosterlessAllocation = true;
            facts.MaxPlayers = maxPlayers;
            return facts;
        }

        /// <summary>A hosted game server whose open session received backfill <see cref="Backfill"/> carrying the ticket.</summary>
        public static AdmissionFacts HostedBackfill(int party = 1)
        {
            AdmissionFacts facts = HostedSession(party);
            facts.BackfillDelivered = true;
            facts.BackfillAllocationId = Backfill;
            facts.BackfillSessionId = Allocation;
            return facts;
        }

        public static AdmissionFacts HostedHold(int? seats, params string[] playerIds)
        {
            AdmissionFacts facts = Facts(HostingMode.Hosted);
            facts.Reservation = ReservationEvidence.HoldFound;
            facts.ReservationSeats = seats;
            facts.ReservationPlayerIds = playerIds.Length == 0 ? null : playerIds;
            return facts;
        }

        public static AdmissionFacts Verified(HostingMode mode, ReservationEvidence verdict, int? seats = null, params string[] playerIds)
        {
            AdmissionFacts facts = Facts(mode);
            facts.Reservation = verdict;
            facts.ReservationSeats = seats;
            facts.ReservationPlayerIds = playerIds.Length == 0 ? null : playerIds;
            return facts;
        }

        public static AdmissionFacts With(this AdmissionFacts facts, Action<AdmissionFacts> change)
        {
            AdmissionFacts copy = facts.Clone();
            change(copy);
            return copy;
        }
    }

    /// <summary>A manual clock: delays complete only when <see cref="Advance"/> moves time past them.</summary>
    internal sealed class ManualScheduler : IScheduler
    {
        private readonly object gate = new object();
        private readonly List<(DateTimeOffset Due, TaskCompletionSource<bool> Done)> waits = new List<(DateTimeOffset, TaskCompletionSource<bool>)>();
        private DateTimeOffset now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (gate)
                {
                    return now;
                }
            }
        }

        public int PendingDelays
        {
            get
            {
                lock (gate)
                {
                    return waits.Count;
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                waits.Add((now + delay, done));
            }

            cancellationToken.Register(() =>
            {
                lock (gate)
                {
                    waits.RemoveAll(w => w.Done == done);
                }

                done.TrySetCanceled();
            });
            return done.Task;
        }

        public void Advance(TimeSpan by)
        {
            var due = new List<TaskCompletionSource<bool>>();
            lock (gate)
            {
                now += by;
                foreach ((DateTimeOffset at, TaskCompletionSource<bool> done) in waits)
                {
                    if (at <= now)
                    {
                        due.Add(done);
                    }
                }

                waits.RemoveAll(w => w.Due <= now);
            }

            foreach (TaskCompletionSource<bool> done in due)
            {
                done.TrySetResult(true);
            }
        }
    }
}
