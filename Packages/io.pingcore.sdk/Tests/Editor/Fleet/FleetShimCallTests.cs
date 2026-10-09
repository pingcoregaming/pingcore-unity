using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The whole shim against <see cref="FakeLocalSdkEndpoint"/>: counters, joinable records, backfills, reservations and the integration rule.</summary>
    public sealed class FleetShimCallTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task EveryUnaryRequestSendsConnectionCloseSoNoPooledConnectionOutlivesNodesIdleTimeout()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.SetCounterAsync("players", 0, None);
                await h.Sdk.ReadyAsync(None);
                await h.Sdk.ListReservationsAsync(None);

                FakeRequest[] unary = h.Fake.Requests.Where(r => r.Path != "/watch/gameserver").ToArray();
                Assert.That(unary.Select(r => r.Method).Distinct(), Is.EquivalentTo(new[] { "GET", "PATCH", "POST" }), "reads and writes both covered");
                foreach (FakeRequest request in unary)
                {
                    Assert.That(request.Connection, Is.Not.Null.And.EqualTo("close").IgnoreCase, request.ToString());
                }
            }
        }

        [Test]
        public async Task AReservationThatArrivesInsideTheWindowIsFoundAndOneThatNeverArrivesIsNotFound()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                long future = h.Scheduler.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
                h.Fake.AddReservation(Record("rsv_late", future), hiddenForLookups: 3);
                ReservationLookup late = await h.Sdk.GetReservationAsync("rsv_late", None);
                Assert.That(late.Status, Is.EqualTo(ReservationLookupStatus.Found), late.Message);
                Assert.That(late.Reservation.Seats, Is.EqualTo(2));
                Assert.That(h.Fake.LookupsOf("rsv_late"), Is.EqualTo(4), "three 404s, then the record");

                // Each lookup takes 10 ms of virtual time, as a real call does, so a loop that stops
                // when one more poll would pass the deadline gives up short of it (at 1.82 s here).
                h.Fake.ReservationLookupClock = () =>
                {
                    DateTimeOffset arrived = h.Scheduler.UtcNow;
                    h.Scheduler.Advance(TimeSpan.FromMilliseconds(10));
                    return arrived;
                };
                DateTimeOffset deadline = h.Scheduler.UtcNow + TimeSpan.FromSeconds(2);
                ReservationLookup never = await h.Sdk.GetReservationAsync("rsv_never", None);
                Assert.That(never.Status, Is.EqualTo(ReservationLookupStatus.NotFound));
                Assert.That(never.HttpStatus, Is.EqualTo(404));
                List<DateTimeOffset> asked = h.Fake.LookupTimesOf("rsv_never");
                Assert.That(asked.Count, Is.InRange(2, 9), "asked every 250 ms for 2 s, then gave up");
                Assert.That(asked.Last(), Is.GreaterThanOrEqualTo(deadline), "the last lookup is at the deadline, so the wait is the whole 2 s");
                Assert.That(asked.Last(), Is.LessThan(deadline + TimeSpan.FromMilliseconds(250)), "and no lookup is made a poll past it");
                Assert.That(asked.Take(asked.Count - 1), Has.All.LessThan(deadline), "only the last lookup is at or past the deadline");
                h.Fake.ReservationLookupClock = null;

                h.Fake.AddReservation(Record("rsv_old", h.Scheduler.UtcNow.ToUnixTimeMilliseconds() - 1));
                Assert.That((await h.Sdk.GetReservationAsync("rsv_old", None)).Status, Is.EqualTo(ReservationLookupStatus.Expired));

                ReservationsResult list = await h.Sdk.ListReservationsAsync(None);
                Assert.That(list.IsOk, Is.True);
                Assert.That(list.Reservations.Select(r => r.ReservationId), Is.EquivalentTo(new[] { "rsv_late", "rsv_old" }));
                Assert.That(h.Fake.Integrated, Is.False, "reservation reads never integrate");
            }
        }

        [Test]
        public async Task AnOldSupervisorWithoutReservationRoutesIsUnsupportedAndAForeign404IsAnError()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                h.Fake.FailNext("/pingcore/reservations/r1", 404, "<!DOCTYPE html><pre>Cannot GET /pingcore/reservations/r1</pre>");
                Assert.That((await h.Sdk.GetReservationAsync("r1", None)).Status, Is.EqualTo(ReservationLookupStatus.Unsupported));
                Assert.That(h.Fake.LookupsOf("r1"), Is.EqualTo(0), "not retried");

                h.Fake.FailNext("/pingcore/reservations/r2", 501, "{\"error\":\"not_implemented\"}");
                Assert.That((await h.Sdk.GetReservationAsync("r2", None)).Status, Is.EqualTo(ReservationLookupStatus.Unsupported));

                h.Fake.FailNext("/pingcore/reservations/r3", 404, "{\"message\":\"no such thing\"}");
                Assert.That((await h.Sdk.GetReservationAsync("r3", None)).Status, Is.EqualTo(ReservationLookupStatus.Error));

                h.Fake.FailNext("/pingcore/reservations", 501, "{\"error\":\"not_implemented\"}");
                Assert.That((await h.Sdk.ListReservationsAsync(None)).Outcome, Is.EqualTo(FleetCallOutcome.Unsupported));
            }
        }

        [Test]
        public async Task CountersParseTheEchoedStringsAndFallBackToTheFleetCapacity()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                CounterResult set = await h.Sdk.SetCounterAsync("players", 3, None);
                Assert.That((set.Name, set.Count, set.Capacity), Is.EqualTo(("players", (long?)3, (long?)8)));
                Assert.That(JObject.Parse(h.Fake.Requests.Last(r => r.Method == "PATCH").Body).ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo("{\"count\":3}"), "the shim sends count only");

                CounterResult read = await h.Sdk.GetCounterAsync("players", None);
                Assert.That((read.Count, read.Capacity), Is.EqualTo(((long?)3, (long?)8)));
                await Wait.Until(() => h.Sdk.Current.Counters["players"].Count == 3, "the frame carries the count");

                CounterResult missing = await h.Sdk.GetCounterAsync("kills", None);
                Assert.That(missing.Outcome, Is.EqualTo(FleetCallOutcome.Rejected));
                Assert.That(missing.Status, Is.EqualTo(404));
                Assert.That(missing.Message, Is.EqualTo("counter kills not found"));

                h.Fake.FailNext("/v1beta1/counters/players", 200, "{\"name\":\"players\",\"count\":\"lots\",\"capacity\":\"8\"}");
                CounterResult garbled = await h.Sdk.GetCounterAsync("players", None);
                Assert.That(garbled.Count, Is.Null, "a count that is not an int64 string is not invented");
                Assert.That(garbled.Capacity, Is.EqualTo(8));
            }
        }

        [Test]
        public async Task AJoinableRecordIsLocallyAcceptedARefusalCarriesTheFieldAndWithdrawIsIdempotent()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                var request = new JoinableSessionRequest { Queue = "ranked", OpenSeats = 3, SessionSize = 8, TtlSeconds = 60 };
                JoinablePublishResult published = await h.Sdk.PublishJoinableAsync("match-83a1", request, None);
                Assert.That(published.LocallyAccepted, Is.True, published.Message);
                Assert.That(published.Record.SessionId, Is.EqualTo("match-83a1"));
                Assert.That(published.Record.OpenSeats, Is.EqualTo(3));
                Assert.That(h.Fake.Joinable, Is.Not.Null);

                JoinablePublishResult refused = await h.Sdk.PublishJoinableAsync("match-83a1", new JoinableSessionRequest { OpenSeats = -1 }, None);
                Assert.That(refused.LocallyAccepted, Is.False);
                Assert.That(refused.Outcome, Is.EqualTo(FleetCallOutcome.Rejected));
                Assert.That(refused.Message, Does.Contain("openSeats"));

                Assert.That((await h.Sdk.WithdrawJoinableAsync("match-83a1", None)).IsOk, Is.True);
                Assert.That((await h.Sdk.WithdrawJoinableAsync("match-83a1", None)).IsOk, Is.True, "a second withdraw is fine");
                Assert.That(h.Fake.Joinable, Is.Null);
            }
        }

        [Test]
        public async Task BackfillsAreReadInTheDocumentedShape()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                h.Fake.AddBackfill(JObject.Parse("{\"allocationId\":\"b7e2\",\"sessionId\":\"match-1\",\"claims\":{\"players\":2},\"context\":{\"roster\":[{\"ticketId\":\"t1\",\"partySize\":2}]},\"deliveredAt\":1234}"));
                BackfillsResult result = await h.Sdk.GetBackfillsAsync(None);
                Assert.That(result.IsOk, Is.True);
                BackfillView backfill = result.Backfills.Single();
                Assert.That(backfill.AllocationId, Is.EqualTo("b7e2"));
                Assert.That(backfill.SessionId, Is.EqualTo("match-1"));
                Assert.That((int)backfill.Context["roster"][0]["partySize"], Is.EqualTo(2));
                Assert.That(backfill.DeliveredAt, Is.EqualTo(1234));
                await Wait.Until(() => h.Sdk.Current.BackfillId == "b7e2", "the latest backfill rides on the view");
            }
        }

        [Test]
        public async Task GetsNeverChangeTheStateOrIntegrate()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                h.Fake.AddReservation(Record("rsv_1", h.Scheduler.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()));
                int transitions = h.States.Count;
                await h.Sdk.GetCounterAsync("players", None);
                await h.Sdk.GetBackfillsAsync(None);
                await h.Sdk.ListReservationsAsync(None);
                await h.Sdk.GetReservationAsync("rsv_1", None);
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.NotReady));
                Assert.That(h.States.Count, Is.EqualTo(transitions));
                Assert.That(h.Fake.Integrated, Is.False);

                await h.Sdk.WithdrawJoinableAsync("nothing", None);
                Assert.That(h.Fake.Integrated, Is.True, "a write, even a no-op one, integrates: the flag can fail");
            }
        }

        [Test]
        public async Task AWriteToAnUnknownRouteIsTheJson501AndDoesNotIntegrate()
        {
            using (var fake = FakeLocalSdkEndpoint.Start())
            using (var transport = new LoopbackHttpTransport(TimeSpan.FromSeconds(5)))
            {
                string url = "http://127.0.0.1:" + fake.Port;
                PingCoreHttpResponse unknown = await transport.SendAsync(new PingCoreHttpRequest("POST", url + "/v1/no-such-route", null, "{}"), None);
                Assert.That(unknown.Status, Is.EqualTo(501));
                Assert.That(unknown.Body, Is.EqualTo("{\"error\":\"not_implemented\"}"));
                Assert.That(fake.Integrated, Is.False, "the compat fallback answers without integrating");

                PingCoreHttpResponse ping = await transport.SendAsync(new PingCoreHttpRequest("POST", url + "/health", null, "{}"), None);
                Assert.That(ping.Status, Is.EqualTo(200));
                Assert.That(fake.Integrated, Is.True, "a routed write does, so the flag can fail");
            }
        }

        private static JObject Record(string id, long expiresAt) => new JObject
        {
            ["reservationId"] = id,
            ["serverId"] = "4211",
            ["seats"] = 2,
            ["playerIds"] = new JArray("p-alice", "p-bob"),
            ["context"] = new JObject { ["mode"] = "duel" },
            ["expiresAt"] = expiresAt,
            ["ownerKind"] = "player",
            ["ownerPlayerId"] = "p-alice",
        };
    }
}
