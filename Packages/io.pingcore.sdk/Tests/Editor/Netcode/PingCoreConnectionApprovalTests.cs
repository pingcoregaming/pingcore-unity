using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Handshake;
using Unity.Netcode;
using UnityEngine;

namespace PingCore.Netcode.NGO.Tests.Editor
{
    /// <summary>
    /// <see cref="PingCoreConnectionApproval"/> on a real <see cref="NetworkManager"/> in the test runner's scene, with no
    /// transport: the tests call <c>ConnectionApprovalCallback</c> directly with a request, as NGO's
    /// <c>ApproveConnection</c> does, and read the response NGO's <c>ProcessPendingApprovals</c> would read.
    /// </summary>
    public sealed class PingCoreConnectionApprovalTests
    {
        private const int Protocol = 3;
        private GameObject host;
        private NetworkManager manager;
        private readonly List<AdmissionDecision> decided = new List<AdmissionDecision>();

        /// <summary>Delays complete only when <see cref="Advance"/> passes them.</summary>
        private sealed class ManualClock : IScheduler
        {
            private readonly List<(DateTimeOffset Due, TaskCompletionSource<bool> Done)> waits = new List<(DateTimeOffset, TaskCompletionSource<bool>)>();

            public DateTimeOffset UtcNow { get; private set; } = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                var done = new TaskCompletionSource<bool>();
                waits.Add((UtcNow + delay, done));
                cancellationToken.Register(() => done.TrySetCanceled());
                return done.Task;
            }

            public void Advance(TimeSpan by)
            {
                UtcNow += by;
                foreach ((DateTimeOffset due, TaskCompletionSource<bool> done) in waits.ToArray())
                {
                    if (due <= UtcNow)
                    {
                        done.TrySetResult(true);
                    }
                }
            }
        }

        /// <summary>Evidence that never answers until cancelled.</summary>
        private sealed class SilentEvidence : IAdmissionEvidence
        {
            public string Source => "silent";

            public bool IsStopping => false;

            public Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken) =>
                Task.Delay(Timeout.Infinite, cancellationToken);
        }

        [SetUp]
        public void SetUp()
        {
            // In the scene the test runner already has open (an additive new scene is refused while that
            // one is untitled and unsaved); DontSave keeps it out of any save, and TearDown destroys it.
            host = new GameObject("PingCoreApprovalTestNetworkManager") { hideFlags = HideFlags.DontSave };
            manager = host.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig();
            decided.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null)
            {
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
            }
        }

        private PingCoreConnectionApproval Install(IAdmissionEvidence evidence = null, ApprovalOptions options = null, IAdmissionGate gate = null)
        {
            options = options ?? new ApprovalOptions { ProtocolVersion = Protocol, Mode = HostingMode.Listen, LanOnly = true, Scheduler = new ManualClock() };
            var approval = new PingCoreConnectionApproval(manager, options, evidence ?? new LanAdmissionEvidence(), gate);
            approval.Decided += d => decided.Add(d);
            approval.Install();
            return approval;
        }

        private NetworkManager.ConnectionApprovalResponse Ask(ulong clientId, byte[] payload)
        {
            var response = new NetworkManager.ConnectionApprovalResponse();
            manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { Payload = payload, ClientNetworkId = clientId }, response);
            return response;
        }

        private static async Task Settled(NetworkManager.ConnectionApprovalResponse response, int timeoutMs = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (response.Pending)
            {
                if (clock.ElapsedMilliseconds > timeoutMs)
                {
                    Assert.Fail("the approval stayed pending for " + timeoutMs + " ms");
                }

                await Task.Delay(10);
            }
        }

        private void Disconnect(ulong clientId)
        {
            // NGO raises OnClientDisconnectCallback from its connection manager; with no transport, invoke it as NGO does.
            object connections = typeof(NetworkManager).GetField("ConnectionManager", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            connections.GetType().GetMethod("InvokeOnClientDisconnectCallback", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(connections, new object[] { clientId });
        }

        [Test]
        public void InstallTurnsApprovalOnRaisesTheBufferAndRefusesASecondCallback()
        {
            Assert.That(manager.NetworkConfig.ClientConnectionBufferTimeout, Is.EqualTo(10), "NGO's default");
            using (PingCoreConnectionApproval approval = Install())
            {
                Assert.That(manager.NetworkConfig.ConnectionApproval, Is.True);
                Assert.That(manager.NetworkConfig.ClientConnectionBufferTimeout, Is.EqualTo(15));
                Assert.That(manager.ConnectionApprovalCallback, Is.Not.Null);
                approval.Install();

                var other = new PingCoreConnectionApproval(manager, new ApprovalOptions { ProtocolVersion = Protocol }, new LanAdmissionEvidence(), null);
                Assert.Throws<InvalidOperationException>(() => other.Install());
            }

            Assert.That(manager.ConnectionApprovalCallback, Is.Null, "Dispose removes the callback");
        }

        [Test]
        public void ALargerBufferIsKeptAndALongerDeadlineRaisesIt()
        {
            manager.NetworkConfig.ClientConnectionBufferTimeout = 40;
            using (Install())
            {
                Assert.That(manager.NetworkConfig.ClientConnectionBufferTimeout, Is.EqualTo(40));
            }

            manager.NetworkConfig.ClientConnectionBufferTimeout = 10;
            using (Install(options: new ApprovalOptions { ProtocolVersion = Protocol, Mode = HostingMode.Listen, LanOnly = true, Deadline = TimeSpan.FromSeconds(20) }))
            {
                Assert.That(manager.NetworkConfig.ClientConnectionBufferTimeout, Is.EqualTo(25), "5 s past the deadline");
            }
        }

        [Test]
        public async Task ABadTicketIsPendingThenRefusedWithItsLiteral()
        {
            using (PingCoreConnectionApproval approval = Install())
            {
                NetworkManager.ConnectionApprovalResponse response = Ask(7, Encoding.UTF8.GetBytes("not a join ticket"));
                Assert.That(response.Pending, Is.True, "the answer always comes on a later step");
                await Settled(response);
                Assert.That(response.Approved, Is.False);
                Assert.That(response.CreatePlayerObject, Is.False);
                Assert.That(response.Reason, Is.EqualTo("payload_malformed"));
                Assert.That(decided, Has.Count.EqualTo(1));
                Assert.That(decided[0].ConnectionId, Is.EqualTo(7));
                Assert.That(decided[0].ReasonWire, Is.EqualTo("payload_malformed"));
                Assert.That(approval.Ledger.Count, Is.EqualTo(0));
                Assert.That(approval.PendingCount, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task AGoodLanTicketIsPendingThenApprovedAndADisconnectGivesTheSeatBack()
        {
            using (PingCoreConnectionApproval approval = Install())
            {
                NetworkManager.ConnectionApprovalResponse response = Ask(8, JoinTicketCodec.Encode(JoinTicket.ForLan("lan-7f3a2c91", Protocol, "Carol")));
                Assert.That(response.Pending, Is.True);
                await Settled(response);
                Assert.That(response.Approved, Is.True, response.Reason);
                Assert.That(response.CreatePlayerObject, Is.True);
                Assert.That(response.Reason, Is.Null);
                Assert.That(decided[0].Approved, Is.True);
                Assert.That(decided[0].Kind, Is.EqualTo(JoinTicketKind.Lan));
                Assert.That(decided[0].EvidenceSource, Is.EqualTo("lan"));
                Assert.That(approval.Ledger.Holds(8), Is.True);

                NetworkManager.ConnectionApprovalResponse twice = Ask(9, JoinTicketCodec.Encode(JoinTicket.ForLan("lan-7f3a2c91", Protocol)));
                await Settled(twice);
                Assert.That(twice.Reason, Is.EqualTo("duplicate_player"));

                Disconnect(8);
                Assert.That(approval.Ledger.Holds(8), Is.False);
                NetworkManager.ConnectionApprovalResponse back = Ask(10, JoinTicketCodec.Encode(JoinTicket.ForLan("lan-7f3a2c91", Protocol)));
                await Settled(back);
                Assert.That(back.Approved, Is.True, "a reconnect after a disconnect is fine");
            }
        }

        [Test]
        public async Task OtherKindsAndProtocolsAreRefusedByALanOnlyListenHost()
        {
            using (Install())
            {
                NetworkManager.ConnectionApprovalResponse reservation = Ask(3, JoinTicketCodec.Encode(JoinTicket.ForReservation("res-1", "anon:p1", Protocol)));
                NetworkManager.ConnectionApprovalResponse older = Ask(4, JoinTicketCodec.Encode(JoinTicket.ForLan("lan-2", Protocol - 1)));
                NetworkManager.ConnectionApprovalResponse empty = Ask(5, null);
                await Settled(reservation);
                await Settled(older);
                await Settled(empty);
                Assert.That(reservation.Reason, Is.EqualTo("kind_not_accepted"));
                Assert.That(older.Reason, Is.EqualTo("protocol_mismatch"));
                Assert.That(empty.Reason, Is.EqualTo("payload_empty"));
            }
        }

        [Test]
        public void TheListenHostsOwnClientIsApprovedAtOnce()
        {
            using (PingCoreConnectionApproval approval = Install())
            {
                NetworkManager.ConnectionApprovalResponse response = Ask(NetworkManager.ServerClientId, null);
                Assert.That(response.Pending, Is.False, "NGO reads the host's answer synchronously");
                Assert.That(response.Approved, Is.True);
                Assert.That(response.CreatePlayerObject, Is.True);
                Assert.That(decided.Only().EvidenceSource, Is.EqualTo("host"));
                Assert.That(approval.Ledger.Count, Is.EqualTo(0), "the host holds no seat");
            }
        }

        [Test]
        public async Task ADecisionPastTheDeadlineIsApprovalTimeout()
        {
            var clock = new ManualClock();
            var options = new ApprovalOptions { ProtocolVersion = Protocol, Mode = HostingMode.Listen, LanOnly = true, Scheduler = clock };
            using (Install(new SilentEvidence(), options))
            {
                NetworkManager.ConnectionApprovalResponse response = Ask(11, JoinTicketCodec.Encode(JoinTicket.ForLan("lan-3", Protocol)));
                await Task.Delay(50);
                Assert.That(response.Pending, Is.True);
                clock.Advance(TimeSpan.FromSeconds(10));
                await Settled(response);
                Assert.That(response.Approved, Is.False);
                Assert.That(response.Reason, Is.EqualTo("approval_timeout"));
                Assert.That(decided.Only().ElapsedMs, Is.EqualTo(10000));
            }
        }

        [Test]
        public async Task ADisconnectWhilePendingAbandonsTheDecisionAndHoldsNoSeat()
        {
            using (PingCoreConnectionApproval approval = Install(new SilentEvidence()))
            {
                NetworkManager.ConnectionApprovalResponse response = Ask(12, JoinTicketCodec.Encode(JoinTicket.ForLan("lan-4", Protocol)));
                await Task.Delay(50);
                Assert.That(approval.PendingCount, Is.EqualTo(1));
                Disconnect(12);
                await Settled(response);
                Assert.That(response.Approved, Is.False);
                Assert.That(approval.PendingCount, Is.EqualTo(0));
                Assert.That(approval.Ledger.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task AfterNotifyStoppingEveryJoinIsStopping()
        {
            using (PingCoreConnectionApproval approval = Install())
            {
                approval.NotifyStopping();
                NetworkManager.ConnectionApprovalResponse response = Ask(13, JoinTicketCodec.Encode(JoinTicket.ForLan("lan-5", Protocol)));
                await Settled(response);
                Assert.That(response.Reason, Is.EqualTo("stopping"));
            }
        }

        [Test]
        public async Task TheDecidedEventCarriesTheTicketRefNeverTheTicketId()
        {
            const string ticketId = "q3Vx9mKc0TzR1bN4wYp7Lg";
            var options = new ApprovalOptions { ProtocolVersion = Protocol, Mode = HostingMode.SelfHosted, Scheduler = new ManualClock() };
            using (Install(new LanAdmissionEvidence(), options))
            {
                NetworkManager.ConnectionApprovalResponse response = Ask(14, JoinTicketCodec.Encode(JoinTicket.ForMatch(ticketId, "alloc-1", "anon:p1", Protocol)));
                await Settled(response);
                Assert.That(response.Reason, Is.EqualTo("kind_not_accepted"));
                AdmissionDecision decision = decided.Only();
                Assert.That(decision.TicketRef, Is.EqualTo(JoinTicket.ForMatch(ticketId, "a", "p", 0).TicketRef));
                Assert.That(decision.ToString(), Does.Not.Contain(ticketId));
                Assert.That(decision.Detail ?? string.Empty, Does.Not.Contain(ticketId));
                foreach (PropertyInfo property in typeof(AdmissionDecision).GetProperties())
                {
                    Assert.That(property.GetValue(decision)?.ToString() ?? string.Empty, Does.Not.Contain(ticketId), property.Name);
                }
            }
        }
    }

    internal static class EnumerableExtensions
    {
        public static T Only<T>(this List<T> list)
        {
            Assert.That(list, Has.Count.EqualTo(1));
            return list[0];
        }
    }
}
