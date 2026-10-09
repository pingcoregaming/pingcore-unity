using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Handshake;
using PingCore.Fleet.Sessions;

namespace PingCore.Fleet.Tests.Editor.Sessions
{
    /// <summary>
    /// The session helpers against <see cref="FakeLocalSdkEndpoint"/> through the real shim: the backfill
    /// watcher and its wait, the hosted admission evidence end to end through <see cref="AdmissionPipeline"/>,
    /// and the joinable keeper. Delays run on the harness's virtual clock.
    /// </summary>
    public sealed class FleetSessionShimTests
    {
        private const string Session = "alloc-1";
        private const string BackfillId = "bf-7";
        private const string TicketId = "H8sJ2dQe5uWm0aZr6tLc3A";
        private static readonly CancellationToken None = CancellationToken.None;

        /// <summary>A deadline that never fires on its own, so a test's virtual waits cannot race it; time comes from the harness clock.</summary>
        internal sealed class NoDeadline : IScheduler
        {
            private readonly IScheduler clock;

            public NoDeadline(IScheduler clock) => this.clock = clock;

            public DateTimeOffset UtcNow => clock.UtcNow;

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        }

        private static JObject BackfillRecord(string id = BackfillId, string sessionId = Session, string playerId = "anon:late") => JObject.Parse(
            "{\"allocationId\":\"" + id + "\",\"sessionId\":\"" + sessionId + "\",\"claims\":{\"players\":1},\"context\":{\"matchmaker\":true,\"backfill\":true," +
            "\"queue\":\"rush-p2\",\"sessionSize\":4,\"players\":1,\"location\":\"ams\",\"sessionId\":\"" + sessionId + "\",\"roster\":[{\"ticketId\":\"" + TicketId +
            "\",\"partySize\":1,\"playerId\":\"" + playerId + "\",\"attributes\":null,\"context\":null}]},\"deliveredAt\":1700000000000}");

        private static async Task<FleetShimHarness> InSession(string context = "{}")
        {
            var h = new FleetShimHarness();
            await h.Sdk.StartAsync(None);
            await h.Sdk.ReadyAsync(None);
            h.Fake.Allocate(Session, context);
            await Wait.Until(() => h.ReceivedCount == 1, "the allocation");
            return h;
        }

        // ---- BackfillWatcher --------------------------------------------------------------------------

        [Test]
        public async Task ANewBackfillAnnotationIsReadAndRaisedOncePerBackfillId()
        {
            using (FleetShimHarness h = await InSession())
            using (var watcher = new BackfillWatcher(h.Sdk, h.Scheduler))
            {
                var raised = new List<BackfillContext>();
                watcher.BackfillReceived += b => { lock (raised) { raised.Add(b); } };
                h.Fake.AddBackfill(BackfillRecord());
                await Wait.Until(() => { lock (raised) { return raised.Count == 1; } }, "the backfill to be raised");
                Assert.That(raised[0].AllocationId, Is.EqualTo(BackfillId));
                Assert.That(raised[0].SessionId, Is.EqualTo(Session));
                Assert.That(raised[0].Roster.Single().TicketId, Is.EqualTo(TicketId));
                Assert.That(watcher.TryGet(BackfillId, out _), Is.True);

                // Further frames carrying the same annotation (a counter write) and a manual refresh raise nothing new.
                await h.Sdk.SetCounterAsync("players", 3, None);
                await watcher.RefreshAsync(None);
                await Wait.For(100);
                Assert.That(raised.Count, Is.EqualTo(1));

                h.Fake.AddBackfill(BackfillRecord("bf-8"));
                await Wait.Until(() => { lock (raised) { return raised.Count == 2; } }, "the second backfill");
                Assert.That(raised[1].AllocationId, Is.EqualTo("bf-8"));
                Assert.That(watcher.Live.Select(b => b.AllocationId), Is.EquivalentTo(new[] { BackfillId, "bf-8" }));
                Assert.That(h.Fake.Integrated, Is.True, "Ready integrated the game");
                Assert.That(h.Fake.Requests.Where(r => r.Path == "/v1/backfills").All(r => r.Method == "GET"), Is.True);
            }
        }

        [Test]
        public async Task AJoinThatOutrunsTheAnnotationWaitsForItsBackfill()
        {
            using (FleetShimHarness h = await InSession())
            using (var watcher = new BackfillWatcher(h.Sdk, h.Scheduler))
            {
                DateTimeOffset start = h.Scheduler.UtcNow;
                Task<BackfillContext> waiting = watcher.WaitForAsync(BackfillId, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), None);
                await Wait.Until(() => h.Fake.Count("GET", "/v1/backfills") >= 2, "the wait to poll at least twice");
                Assert.That(waiting.IsCompleted, Is.False, "no backfill yet");
                h.Fake.AddBackfill(BackfillRecord());
                BackfillContext found = await waiting;
                Assert.That(found, Is.Not.Null);
                Assert.That(found.AllocationId, Is.EqualTo(BackfillId));
                Assert.That(h.Scheduler.UtcNow - start, Is.LessThan(TimeSpan.FromSeconds(5)));
            }
        }

        [Test]
        public async Task ABackfillThatNeverArrivesEndsTheWaitAfterFiveSecondsOfSchedulerTime()
        {
            using (FleetShimHarness h = await InSession())
            using (var watcher = new BackfillWatcher(h.Sdk, h.Scheduler))
            {
                DateTimeOffset start = h.Scheduler.UtcNow;
                BackfillContext found = await watcher.WaitForAsync("bf-never", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), None);
                Assert.That(found, Is.Null);
                Assert.That(h.Scheduler.UtcNow - start, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(5)));
                Assert.That(h.Fake.Count("GET", "/v1/backfills"), Is.InRange(10, 12), "asked every 500 ms for 5 s");
                Assert.That(h.Scheduler.Delays.Where(d => d <= TimeSpan.FromMilliseconds(500)).Count(), Is.GreaterThanOrEqualTo(10));
            }
        }

        [Test]
        public async Task ANewSessionForgetsTheOldSessionsBackfills()
        {
            using (FleetShimHarness h = await InSession())
            using (var watcher = new BackfillWatcher(h.Sdk, h.Scheduler))
            {
                h.Fake.AddBackfill(BackfillRecord());
                await Wait.Until(() => watcher.TryGet(BackfillId, out _), "the backfill");
                await h.Sdk.EndSessionAsync(Session, None);
                await Wait.Until(() => h.ClearedCount == 1, "the session to end");
                await Wait.Until(() => !watcher.TryGet(BackfillId, out _), "the old backfill to be forgotten");
            }
        }

        // ---- HostedAdmissionEvidence through the pipeline ---------------------------------------------

        internal sealed class CountingGate : IAdmissionGate
        {
            public int Calls { get; private set; }

            public string RefusePlayer { get; set; }

            public AdmissionGateResult CanAdmit(JoinTicket ticket, AdmissionFacts facts)
            {
                Calls++;
                return ticket.PlayerId == RefusePlayer ? AdmissionGateResult.Reject(JoinRejectReason.RefusedByGame, "refused") : AdmissionGateResult.Admit;
            }
        }

        internal static (AdmissionPipeline Pipeline, BackfillWatcher Watcher) Hosted(FleetShimHarness h, IAdmissionGate gate = null, bool allowSelfAllocatedJoins = false, bool claimIdleSessions = false)
        {
            var watcher = new BackfillWatcher(h.Sdk, h.Scheduler);
            var options = new ApprovalOptions { ProtocolVersion = 3, Mode = HostingMode.Hosted, AllowSelfAllocatedJoins = allowSelfAllocatedJoins, ClaimIdleSessions = claimIdleSessions };
            return (new AdmissionPipeline(options, new HostedAdmissionEvidence(h.Sdk, watcher), gate, new NoDeadline(h.Scheduler)), watcher);
        }

        [Test]
        public async Task WithAllowSelfAllocatedJoinsAMatchJoinIntoASelfAllocationIsAdmittedOnItsAllocationIdAloneUpToThePlayersCapacity()
        {
            const string self = "self-123";
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                h.Fake.Allocate(self, "{}");
                await Wait.Until(() => h.ReceivedCount == 1, "the self-allocation");
                var gate = new CountingGate { RefusePlayer = "anon:refused" };
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = Hosted(h, gate, allowSelfAllocatedJoins: true);
                using (watcher)
                {
                    ulong connection = 0;
                    Func<string, string, string, Task<AdmissionDecision>> join = (ticket, allocation, player) =>
                        pipeline.AdmitAsync(++connection, JoinTicketCodec.Encode(JoinTicket.ForMatch(ticket, allocation, player, 3)), None);

                    AdmissionDecision first = await join(TicketId, self, "anon:p0");
                    Assert.That(first.Approved, Is.True, first.Detail);
                    Assert.That(first.PartySize, Is.Null, "no roster entry, so no party size");
                    Assert.That((await join(TicketId, "self-999", "anon:x")).ReasonWire, Is.EqualTo("allocation_mismatch"));
                    Assert.That((await join("another-ticket", self, "anon:p0")).ReasonWire, Is.EqualTo("duplicate_player"));
                    Assert.That((await join("another-ticket", self, "anon:refused")).ReasonWire, Is.EqualTo("refused_by_game"), "the game's gate still decides");

                    // The fake's players counter has capacity 8: seven more players, each on a ticket id of its own.
                    for (int i = 1; i < 8; i++)
                    {
                        AdmissionDecision next = await join("ticket-" + i, self, "anon:p" + i);
                        Assert.That(next.Approved, Is.True, "player " + i + ": " + next.Detail);
                    }

                    Assert.That(pipeline.Ledger.AdmittedForAllocation(self), Is.EqualTo(8));
                    Assert.That((await join("ticket-8", self, "anon:p8")).ReasonWire, Is.EqualTo("roster_full"));
                    Assert.That(pipeline.Release(1), Is.True, "the first player leaves");
                    Assert.That((await join("ticket-8", self, "anon:p8")).Approved, Is.True, "and gives the seat back");
                    Assert.That(gate.Calls, Is.EqualTo(10), "asked for every join the table accepted");
                    Assert.That(h.Fake.Requests.Any(r => r.Path.Contains("verify")), Is.False, "the hosted path never calls verify");
                }
            }
        }

        [Test]
        public void ARosterlessAllocationsCapIsThePlayersCapacityElseTheConfiguredMaxPlayers()
        {
            GameServerSnapshot View(string capacity) => new GameServerSnapshot(new global::PingCore.Fleet.Wire.GameServerView
            {
                Status = new global::PingCore.Fleet.Wire.GameServerStatus
                {
                    Counters = new Dictionary<string, global::PingCore.Fleet.Wire.CounterView> { ["players"] = new global::PingCore.Fleet.Wire.CounterView { Count = "0", Capacity = capacity } },
                },
            });

            Assert.That(HostedAdmissionEvidence.PlayerCap(View("8"), 3), Is.EqualTo(8));
            Assert.That(HostedAdmissionEvidence.PlayerCap(View("0"), 3), Is.EqualTo(3), "a capacity of 0 is none");
            Assert.That(HostedAdmissionEvidence.PlayerCap(View(null), 3), Is.EqualTo(3), "no capacity on the counter");
            Assert.That(HostedAdmissionEvidence.PlayerCap(View("not-a-number"), 0), Is.EqualTo(0), "0 is no limit");
            Assert.That(HostedAdmissionEvidence.PlayerCap(View("9999999999"), 3), Is.EqualTo(int.MaxValue));
            Assert.That(HostedAdmissionEvidence.PlayerCap(new GameServerSnapshot(new global::PingCore.Fleet.Wire.GameServerView()), 5), Is.EqualTo(5), "no players counter");
            Assert.That(HostedAdmissionEvidence.PlayerCap(null, 5), Is.EqualTo(5), "no view yet");
        }

        [Test]
        public async Task AHostedBackfillJoinBeforeItsAnnotationIsAdmittedOnceTheBackfillArrives()
        {
            using (FleetShimHarness h = await InSession())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = Hosted(h);
                using (watcher)
                {
                    byte[] payload = JoinTicketCodec.Encode(JoinTicket.ForBackfill(TicketId, BackfillId, "anon:late", 3));
                    Task<AdmissionDecision> pending = pipeline.AdmitAsync(11, payload, None);
                    await Wait.Until(() => h.Fake.Count("GET", "/v1/backfills") >= 2, "the evidence to poll");
                    Assert.That(pending.IsCompleted, Is.False);
                    h.Fake.AddBackfill(BackfillRecord());
                    AdmissionDecision decision = await pending;
                    Assert.That(decision.Approved, Is.True, decision.Detail);
                    Assert.That(decision.Kind, Is.EqualTo(JoinTicketKind.Backfill));
                    Assert.That(decision.EvidenceSource, Is.EqualTo("fleet"));
                    Assert.That(decision.PartySize, Is.EqualTo(1));
                    Assert.That(pipeline.Ledger.AdmittedForTicket(BackfillId, TicketId), Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task AHostedBackfillJoinWhoseBackfillNeverArrivesIsBackfillUnknownAfterFiveSeconds()
        {
            using (FleetShimHarness h = await InSession())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = Hosted(h);
                using (watcher)
                {
                    DateTimeOffset start = h.Scheduler.UtcNow;
                    byte[] payload = JoinTicketCodec.Encode(JoinTicket.ForBackfill(TicketId, "bf-forged", "anon:late", 3));
                    AdmissionDecision decision = await pipeline.AdmitAsync(12, payload, None);
                    Assert.That(decision.ReasonWire, Is.EqualTo("backfill_unknown"), decision.Detail);
                    Assert.That(h.Scheduler.UtcNow - start, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(5)));
                    Assert.That(decision.ElapsedMs, Is.GreaterThanOrEqualTo(5000));
                    Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
                }
            }
        }

        [Test]
        public async Task AHostedBackfillIntoAnotherSessionIsBackfillUnknownAtOnce()
        {
            using (FleetShimHarness h = await InSession())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = Hosted(h);
                using (watcher)
                {
                    h.Fake.AddBackfill(BackfillRecord(sessionId: "alloc-old"));
                    await Wait.Until(() => watcher.TryGet(BackfillId, out _), "the backfill");
                    AdmissionDecision decision = await pipeline.AdmitAsync(13, JoinTicketCodec.Encode(JoinTicket.ForBackfill(TicketId, BackfillId, "anon:late", 3)), None);
                    Assert.That(decision.ReasonWire, Is.EqualTo("backfill_unknown"));
                }
            }
        }

        [Test]
        public async Task AHostedMatchJoinIsCheckedAgainstTheAllocationsRoster()
        {
            string context = "{\"matchmaker\":true,\"queue\":\"rush-p2\",\"sessionSize\":2,\"players\":2,\"relaxed\":false,\"backfill\":false,\"location\":null," +
                "\"roster\":[{\"ticketId\":\"" + TicketId + "\",\"partySize\":2,\"playerId\":\"anon:a\",\"attributes\":null,\"context\":null}]}";
            using (FleetShimHarness h = await InSession(context))
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = Hosted(h);
                using (watcher)
                {
                    ulong connection = 0;
                    Func<string, string, string, Task<AdmissionDecision>> join = (ticket, allocation, player) =>
                        pipeline.AdmitAsync(++connection, JoinTicketCodec.Encode(JoinTicket.ForMatch(ticket, allocation, player, 3)), None);
                    Assert.That((await join(TicketId, Session, "anon:a")).Approved, Is.True, "the submitter");
                    Assert.That((await join(TicketId, Session, "anon:b")).Approved, Is.True, "a party member with their own id");
                    Assert.That((await join(TicketId, Session, "anon:c")).ReasonWire, Is.EqualTo("roster_full"));
                    Assert.That((await join("forged", Session, "anon:d")).ReasonWire, Is.EqualTo("not_in_roster"));
                    Assert.That((await join(TicketId, "alloc-other", "anon:e")).ReasonWire, Is.EqualTo("allocation_mismatch"));
                    Assert.That((await join(TicketId, Session, "anon:a")).ReasonWire, Is.EqualTo("duplicate_player"));
                }
            }
        }

        [Test]
        public async Task AHostedReservationJoinReadsThePushedHoldAndAnUnknownOneIsInvalidAfterTheLookAgain()
        {
            using (FleetShimHarness h = await InSession())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = Hosted(h);
                using (watcher)
                {
                    h.Fake.AddReservation(new JObject
                    {
                        ["reservationId"] = "rsv-1", ["serverId"] = "4211", ["seats"] = 2, ["playerIds"] = new JArray("anon:a", "anon:b"),
                        ["context"] = null, ["expiresAt"] = h.Scheduler.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(), ["ownerKind"] = "player", ["ownerPlayerId"] = "anon:a",
                    }, hiddenForLookups: 2);
                    AdmissionDecision held = await pipeline.AdmitAsync(1, JoinTicketCodec.Encode(JoinTicket.ForReservation("rsv-1", "anon:b", 3)), None);
                    Assert.That(held.Approved, Is.True, held.Detail);
                    Assert.That(h.Fake.LookupsOf("rsv-1"), Is.EqualTo(3), "a trailing push is looked for again");

                    AdmissionDecision stranger = await pipeline.AdmitAsync(2, JoinTicketCodec.Encode(JoinTicket.ForReservation("rsv-1", "anon:z", 3)), None);
                    Assert.That(stranger.ReasonWire, Is.EqualTo("reservation_invalid"));

                    DateTimeOffset start = h.Scheduler.UtcNow;
                    AdmissionDecision unknown = await pipeline.AdmitAsync(3, JoinTicketCodec.Encode(JoinTicket.ForReservation("rsv-made-up", "anon:y", 3)), None);
                    Assert.That(unknown.ReasonWire, Is.EqualTo("reservation_invalid"));
                    Assert.That(h.Scheduler.UtcNow - start, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(2)));
                    Assert.That(h.Fake.Requests.Any(r => r.Path.Contains("verify")), Is.False, "the hosted path never calls verify");
                }
            }
        }

        [Test]
        public async Task AStoppingShimRefusesEveryJoinAsStopping()
        {
            using (FleetShimHarness h = await InSession())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = Hosted(h);
                using (watcher)
                {
                    h.Sdk.NotifyProcessStopping();
                    AdmissionDecision decision = await pipeline.AdmitAsync(1, JoinTicketCodec.Encode(JoinTicket.ForMatch(TicketId, Session, "anon:a", 3)), None);
                    Assert.That(decision.ReasonWire, Is.EqualTo("stopping"));
                }
            }
        }

        [Test]
        public void TheHoldLookupMapsEveryStatus()
        {
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.Found), Is.EqualTo(ReservationEvidence.HoldFound));
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.NotFound), Is.EqualTo(ReservationEvidence.HoldNotFound));
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.Expired), Is.EqualTo(ReservationEvidence.HoldExpired));
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.Unreachable), Is.EqualTo(ReservationEvidence.HoldUnreachable));
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.Cancelled), Is.EqualTo(ReservationEvidence.HoldUnreachable));
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.Error), Is.EqualTo(ReservationEvidence.HoldError));
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.Unsupported), Is.EqualTo(ReservationEvidence.HoldError));
            Assert.That(HostedAdmissionEvidence.MapLookup(ReservationLookupStatus.Inert), Is.EqualTo(ReservationEvidence.HoldError));
        }

        // ---- JoinableSessionKeeper --------------------------------------------------------------------

        private static async Task SetPlayers(FleetShimHarness h, long count)
        {
            Assert.That((await h.Sdk.SetCounterAsync("players", count, None)).IsOk, Is.True);
            await Wait.Until(() => h.Sdk.Current.Counters.TryGetValue("players", out GameServerCounter c) && c.Count == count, "the counter on the view");
        }

        private static int OpenSeatsOnFake(FleetShimHarness h) => h.Fake.Joinable == null ? -1 : (int)h.Fake.Joinable["openSeats"];

        private int Publishes(FleetShimHarness h) => h.Fake.Count("POST", "/v1/sessions/" + Session + "/joinable");

        private int Withdraws(FleetShimHarness h) => h.Fake.Count("DELETE", "/v1/sessions/" + Session + "/joinable");

        [Test]
        public async Task TheKeeperPublishesOnTheFirstUpdateAndRepublishesWhenSeatsChange()
        {
            using (FleetShimHarness h = await InSession())
            {
                JoinableSessionKeeper keeper = JoinableSessionKeeper.Start(h.Sdk, Session, "rush-p2", 4, 8, 30, h.Scheduler);
                await Wait.For(100);
                Assert.That(Publishes(h), Is.EqualTo(0), "nothing before the first Update");

                keeper.Update(2, 0);
                await Wait.Until(() => OpenSeatsOnFake(h) == 6, "openSeats 6");
                Assert.That((string)h.Fake.Joinable["queue"], Is.EqualTo("rush-p2"));
                Assert.That((int)h.Fake.Joinable["sessionSize"], Is.EqualTo(4));
                Assert.That((int)h.Fake.Joinable["ttlSeconds"], Is.EqualTo(30));
                Assert.That(keeper.LiveSeats, Is.EqualTo(6));
                Assert.That(keeper.LastPublish.LocallyAccepted, Is.True);

                keeper.Update(2, 1);
                await Wait.Until(() => OpenSeatsOnFake(h) == 5, "openSeats 5 for an expected joiner");
                keeper.Update(3, 0);
                await Wait.For(100);
                Assert.That(OpenSeatsOnFake(h), Is.EqualTo(5), "the joiner arriving changes nothing");
                await keeper.StopAsync(None);
            }
        }

        [Test]
        public async Task TheKeeperRepublishesEveryHalfTtlWithoutAChange()
        {
            using (FleetShimHarness h = await InSession())
            {
                JoinableSessionKeeper keeper = JoinableSessionKeeper.Start(h.Sdk, Session, "rush-p2", 4, 8, 30, h.Scheduler);
                keeper.Update(1, 0);
                await Wait.Until(() => Publishes(h) == 1, "the first publish");
                DateTimeOffset first = h.Scheduler.UtcNow;
                await Wait.Until(() => Publishes(h) >= 3, "two republishes with nothing changed");
                Assert.That(h.Scheduler.UtcNow - first, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(30)));
                Assert.That(h.Scheduler.Delays, Has.Some.InRange(TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(15)), "the keeper waited ttl / 2");
                await keeper.StopAsync(None);
            }
        }

        [Test]
        public async Task TheKeeperClampsToThePlayersFreeCapacityAndWithdrawsAtZero()
        {
            using (FleetShimHarness h = await InSession())
            {
                await SetPlayers(h, 2);
                JoinableSessionKeeper keeper = JoinableSessionKeeper.Start(h.Sdk, Session, "rush-p2", 0, 20, 30, h.Scheduler);
                keeper.Update(2, 0);
                await Wait.Until(() => OpenSeatsOnFake(h) == 6, "openSeats clamped to the players capacity 8 minus 2");
                Assert.That(h.Fake.Joinable["sessionSize"], Is.Null, "a session size of 0 is left out");

                await SetPlayers(h, 8);
                keeper.Update(8, 0);
                await Wait.Until(() => Withdraws(h) == 1 && h.Fake.Joinable == null, "the withdraw at 0 seats");
                Assert.That(keeper.LiveSeats, Is.Null);
                int publishes = Publishes(h);
                await Wait.For(100);
                Assert.That(Publishes(h), Is.EqualTo(publishes), "nothing republishes at 0");
                Assert.That(Withdraws(h), Is.EqualTo(1), "and the withdraw is not repeated");

                await SetPlayers(h, 7);
                keeper.Update(7, 0);
                await Wait.Until(() => OpenSeatsOnFake(h) == 1, "a seat frees up: publish again");
                await keeper.StopAsync(None);
            }
        }

        [Test]
        public async Task StopWithdrawsTheRecordAndTheSessionEndingStopsTheKeeper()
        {
            using (FleetShimHarness h = await InSession())
            {
                JoinableSessionKeeper keeper = JoinableSessionKeeper.Start(h.Sdk, Session, "rush-p2", 4, 8, 30, h.Scheduler);
                keeper.Update(1, 0);
                await Wait.Until(() => OpenSeatsOnFake(h) == 7, "the publish");
                FleetCallResult withdrawn = await keeper.StopAsync(None);
                Assert.That(withdrawn.IsOk, Is.True);
                Assert.That(h.Fake.Joinable, Is.Null);
                Assert.That(keeper.IsRunning, Is.False);
                Assert.That(await keeper.StopAsync(None), Is.Null, "a second stop does nothing");

                JoinableSessionKeeper second = JoinableSessionKeeper.Start(h.Sdk, Session, "rush-p2", 4, 8, 30, h.Scheduler);
                second.Update(1, 0);
                await Wait.Until(() => OpenSeatsOnFake(h) == 7, "the second keeper's publish");
                await h.Sdk.EndSessionAsync(Session, None);
                await Wait.Until(() => !second.IsRunning, "the keeper to stop with the session");
                int publishes = Publishes(h);
                int withdraws = Withdraws(h);
                second.Update(2, 0);
                await Wait.For(100);
                Assert.That(Publishes(h), Is.EqualTo(publishes), "a stopped keeper publishes nothing");
                Assert.That(Withdraws(h), Is.EqualTo(withdraws), "and owes no withdraw: ending the session withdrew the record");
            }
        }

        [Test]
        public void TheKeeperRefusesAShortTtlOrNoAllocation()
        {
            using (var h = new FleetShimHarness())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => JoinableSessionKeeper.Start(h.Sdk, Session, "q", 4, 8, 4, h.Scheduler));
                Assert.Throws<ArgumentException>(() => JoinableSessionKeeper.Start(h.Sdk, null, "q", 4, 8, 30, h.Scheduler));
                Assert.Throws<ArgumentNullException>(() => JoinableSessionKeeper.Start(null, Session, "q", 4, 8, 30, h.Scheduler));
            }
        }
    }
}
