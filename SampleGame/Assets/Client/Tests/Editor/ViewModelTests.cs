using System;
using System.Collections.Generic;
using BeaconRush.Client.Models;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client;
using PingCore.Discovery.Client.Wire;

namespace BeaconRush.Client.Tests
{
    public sealed class BrowserModelTests
    {
        private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        private static PublicServer Server(string id, int players, string location)
        {
            return new PublicServer
            {
                ServerId = id, Name = "Game " + id, Ip = "203.0.113.1", Port = 7777, Players = players, MaxPlayers = 8, Version = "v1",
                Meta = location == null ? null : new JObject { ["location"] = location, ["proto"] = 2 },
            };
        }

        [Test]
        public void ATabSendsAtMostOneListRequestEveryFiveSeconds()
        {
            var model = new BrowserModel(2);
            Assert.That(model.TryBeginRefresh(ClientApp.Fleet, T0, out ServerListQuery first), Is.True);
            Assert.That(first, Is.Not.Null);
            Assert.That(model.TryBeginRefresh(ClientApp.Fleet, T0.AddSeconds(1), out _), Is.False, "still loading");
            model.ApplyPage(ClientApp.Fleet, new ServerPage(new[] { Server("a", 1, null) }, 1, 1, 20, 0, null));
            Assert.That(model.TryBeginRefresh(ClientApp.Fleet, T0.AddSeconds(4.9), out _), Is.False, "answered, but less than 5 s since the request");
            Assert.That(model.RefreshWait(ClientApp.Fleet, T0.AddSeconds(4)), Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(model.TryBeginRefresh(ClientApp.Community, T0.AddSeconds(1), out _), Is.True, "each tab has its own limit");
            Assert.That(model.TryBeginRefresh(ClientApp.Fleet, T0.AddSeconds(5), out _), Is.True);
        }

        [Test]
        public void TheQueryCarriesTheFiltersSortPageAndLatency()
        {
            var model = new BrowserModel(2);
            model.SetFilters(true, true, "  rush  ");
            model.SetLatency(new Dictionary<string, int> { ["eu-west-ams"] = 24, ["us-east-nyc"] = 90 });
            model.SetSort(BrowserSort.Latency, false);
            Assert.That(model.BuildQuery(ClientApp.Fleet).ToQueryString(),
                Is.EqualTo("limit=20&offset=0&search=rush&hasSlots=true&meta.proto=2&sort=latency&latency.eu-west-ams=24&latency.us-east-nyc=90"));
        }

        [Test]
        public void ALatencySortWithoutAMeasurementFallsBackToPlayers()
        {
            var model = new BrowserModel(2);
            model.SetSort(BrowserSort.Latency, false);
            Assert.That(model.BuildQuery(ClientApp.Fleet).ToQueryString(), Is.EqualTo("limit=20&offset=0&hasSlots=true&sort=-players"));
        }

        [Test]
        public void PagingMovesByAPageAndAFilterChangeReturnsToTheFirst()
        {
            var model = new BrowserModel(2);
            model.ApplyPage(ClientApp.Fleet, new ServerPage(new[] { Server("a", 1, null) }, 45, 20, 20, 0, new ServerListQuery()));
            Assert.That(model.NextPage(ClientApp.Fleet), Is.True);
            Assert.That(model.BuildQuery(ClientApp.Fleet).ToQueryString(), Does.StartWith("limit=20&offset=20"));
            Assert.That(model.PreviousPage(ClientApp.Fleet), Is.True);
            Assert.That(model.PreviousPage(ClientApp.Fleet), Is.False, "already on the first page");
            model.NextPage(ClientApp.Fleet);
            model.SetFilters(false, false, string.Empty);
            Assert.That(model.Tab(ClientApp.Fleet).Offset, Is.Zero);
            model.ApplyPage(ClientApp.Fleet, new ServerPage(new[] { Server("a", 1, null) }, 1, 1, 20, 0, null));
            Assert.That(model.NextPage(ClientApp.Fleet), Is.False, "no page follows the last");
        }

        [Test]
        public void TheLatencyColumnComesFromTheGameServersLocationMedian()
        {
            var medians = new Dictionary<string, int> { ["eu-west-ams"] = 24 };
            Assert.That(BrowserModel.ToRow(Server("a", 3, "eu-west-ams"), medians).LatencyText, Is.EqualTo("24 ms"));
            Assert.That(BrowserModel.ToRow(Server("b", 3, "us-east-nyc"), medians).LatencyText, Is.EqualTo("-"), "an unmeasured location");
            Assert.That(BrowserModel.ToRow(Server("c", 3, null), medians).LatencyMs, Is.Null, "no meta");
            BrowserRow row = BrowserModel.ToRow(Server("d", 8, "eu-west-ams"), medians);
            Assert.That(row.PlayersText, Is.EqualTo("8/8"));
            Assert.That(row.HasFreeSeat, Is.False);
        }

        [Test]
        public void AFailureKeepsTheLastGoodRowsAndShowsTheReason()
        {
            var model = new BrowserModel(2);
            model.TryBeginRefresh(ClientApp.Community, T0, out _);
            model.ApplyPage(ClientApp.Community, new ServerPage(new[] { Server("a", 1, null) }, 1, 1, 20, 0, null));
            model.TryBeginRefresh(ClientApp.Community, T0.AddSeconds(6), out _);
            model.ApplyFailure(ClientApp.Community, "RateLimited (429)");
            BrowserTab tab = model.Tab(ClientApp.Community);
            Assert.That(tab.Rows.Count, Is.EqualTo(1));
            Assert.That(tab.Error, Is.EqualTo("RateLimited (429)"));
            Assert.That(tab.Loading, Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => model.Tab(ClientApp.SelfHost));
        }
    }

    public sealed class MatchmakingModelTests
    {
        private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        [Test]
        public void TheQueueIsNamedForTheProtocolAndTheTicketRelaxesToTwo()
        {
            var model = new MatchmakingModel(2);
            Assert.That(model.Queue, Is.EqualTo("rush-p2"));
            TicketOptions options = model.CreateTicketOptions(true);
            Assert.That(options.Queue, Is.EqualTo("rush-p2"));
            Assert.That(options.SessionSize, Is.EqualTo(4));
            Assert.That(options.MinSessionSize, Is.EqualTo(2));
            Assert.That(options.RelaxAfterSeconds, Is.EqualTo(10));
            Assert.That(options.JoinInProgress, Is.True);
            Assert.That(options.PartySize, Is.EqualTo(1));
        }

        [Test]
        public void TheStatusFollowsTheTicketAndTheClockFreezesAtTheEnd()
        {
            var model = new MatchmakingModel(2);
            Assert.That(model.CanSearch, Is.True);
            model.BeginSubmit(T0);
            Assert.That(model.CanCancel, Is.True);
            Assert.That(model.CanSearch, Is.False);
            model.Apply(TicketState.Queued, "abc123abc123", null, null, T0.AddSeconds(1));
            Assert.That(model.StatusText(T0.AddSeconds(72)), Is.EqualTo("Searching rush-p2... 1:12"));
            var match = new MatchAssignment("alloc-1", "gs-1", "203.0.113.1", 7777, true, "eu-west-ams");
            model.Apply(TicketState.Matched, "abc123abc123", match, null, T0.AddSeconds(80));
            Assert.That(model.CanCancel, Is.False);
            Assert.That(model.Match, Is.SameAs(match));
            Assert.That(model.StatusText(T0.AddSeconds(500)), Does.Contain("1:20").And.Contain("in progress"));
        }

        [Test]
        public void ARefusalAndAFailureShowTheirReason()
        {
            var model = new MatchmakingModel(2);
            model.BeginSubmit(T0);
            model.Refuse("InvalidRequest: session_too_small", T0);
            Assert.That(model.Status, Is.EqualTo(MatchmakingStatus.Refused));
            Assert.That(model.StatusText(T0), Does.Contain("session_too_small"));
            Assert.That(model.CanSearch, Is.True);
            model.BeginSubmit(T0);
            model.Apply(TicketState.Failed, "r", null, null, T0.AddSeconds(3));
            Assert.That(model.StatusText(T0), Does.Contain("stopped answering"));
            model.Apply(TicketState.Expired, "r", null, null, T0.AddSeconds(3));
            Assert.That(model.Status, Is.EqualTo(MatchmakingStatus.Expired));
            model.Reset();
            Assert.That(model.Status, Is.EqualTo(MatchmakingStatus.Idle));
            Assert.That(model.TicketRef, Is.Null);
        }

        [TestCase(0, "0:00")]
        [TestCase(59.9, "0:59")]
        [TestCase(61, "1:01")]
        [TestCase(-5, "0:00")]
        public void TheClockIsMinutesAndSeconds(double seconds, string expected)
        {
            Assert.That(MatchmakingModel.FormatClock(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
        }
    }

    public sealed class LobbyModelTests
    {
        private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        private static IReadOnlyList<LobbyEntry> Players(int count)
        {
            var list = new List<LobbyEntry>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new LobbyEntry((ulong)(i + 1), "P" + i, i, i == 0));
            }

            return list;
        }

        [Test]
        public void WithoutAReplicatedPhaseTheLobbyWaitsForTheRosterOrTheTimer()
        {
            var model = new LobbyModel();
            model.Join(T0, 4, TimeSpan.FromSeconds(30));
            model.SetRoster(Players(2));
            Assert.That(model.Phase(T0.AddSeconds(10)), Is.EqualTo(SessionView.Lobby));
            Assert.That(model.LobbyText(T0.AddSeconds(9)), Is.EqualTo("2 of 4 players, starting in 0:21"));
            Assert.That(model.Phase(T0.AddSeconds(30)), Is.EqualTo(SessionView.Match), "the timer ran out");
            model.SetRoster(Players(4));
            Assert.That(model.Phase(T0.AddSeconds(1)), Is.EqualTo(SessionView.Match), "the roster is complete");
        }

        [Test]
        public void AReplicatedPhaseAndCountWin()
        {
            var model = new LobbyModel();
            model.Join(T0, 4, TimeSpan.FromSeconds(30));
            model.SetRoster(Players(4));
            model.SetReportedPhase(SessionView.Lobby, 0);
            Assert.That(model.Phase(T0.AddSeconds(100)), Is.EqualTo(SessionView.Lobby));
            Assert.That(model.LobbyText(T0), Is.EqualTo("4 of 4 players, waiting"));
            model.SetReportedPhase(SessionView.Match, 161);
            Assert.That(model.MatchText(T0), Is.EqualTo("Match: 2:41 left"));
            model.SetReportedPhase(SessionView.Results, 9);
            Assert.That(model.Phase(T0), Is.EqualTo(SessionView.Results));
        }

        [Test]
        public void LeavingKeepsTheScoresBestFirst()
        {
            var model = new LobbyModel();
            model.Join(T0, 2, TimeSpan.FromSeconds(30));
            model.SetRoster(new[] { new LobbyEntry(1, "A", 3, true), new LobbyEntry(2, "B", 7, false), new LobbyEntry(3, null, 3, false) });
            model.Leave("match_complete");
            Assert.That(model.InSession, Is.False);
            Assert.That(model.EndReason, Is.EqualTo("match_complete"));
            Assert.That(model.FinalScores[0].Name, Is.EqualTo("B"));
            Assert.That(model.FinalScores[1].ClientId, Is.EqualTo(1UL));
            Assert.That(model.FinalScores[2].Name, Is.EqualTo("Player 3"));
            Assert.That(model.Phase(T0), Is.EqualTo(SessionView.Results));
            Assert.That(model.TimerRemaining(T0), Is.EqualTo(TimeSpan.Zero));
        }
    }

    public sealed class HostModelTests
    {
        [Test]
        public void OnlineNeedsTheEnvironmentTokenAndLanOnlyNeverDoes()
        {
            var noToken = new HostModel(false);
            Assert.That(noToken.Reach, Is.EqualTo(HostReach.LanOnly));
            Assert.That(noToken.SetReach(HostReach.Online), Is.False);
            Assert.That(noToken.Reach, Is.EqualTo(HostReach.LanOnly));
            var withToken = new HostModel(true);
            Assert.That(withToken.Reach, Is.EqualTo(HostReach.Online));
            Assert.That(withToken.SetReach(HostReach.LanOnly), Is.True);
        }

        [TestCase("7777", true)]
        [TestCase("1024", true)]
        [TestCase("65534", true)]
        [TestCase("65535", false)]
        [TestCase("1023", false)]
        [TestCase("77a7", false)]
        [TestCase("", false)]
        [TestCase("-7777", false)]
        public void TheGamePortLeavesRoomForTheEchoPortAbove(string text, bool valid)
        {
            var model = new HostModel(true) { PortText = text };
            Assert.That(model.TryGetPort(out int port, out string problem), Is.EqualTo(valid));
            Assert.That(problem == null, Is.EqualTo(valid));
            if (valid)
            {
                Assert.That(port.ToString(), Is.EqualTo(text));
            }
        }

        [Test]
        public void TheScreenShowsBothPortsToForwardOnline()
        {
            Assert.That(HostModel.PortsToForward(7777), Is.EqualTo("UDP 7777 (game) and UDP 7778 (Discovery's reachability echo)"));
            var model = new HostModel(true);
            model.Starting(7790);
            model.Hosting("Listed as gs-1.");
            Assert.That(model.StatusText(), Does.Contain("UDP 7790").And.Contain("UDP 7791").And.Contain("gs-1"));
            model.SetReach(HostReach.LanOnly);
            Assert.That(model.StatusText(), Does.Not.Contain("forward"));
            model.Fail(null);
            Assert.That(model.StatusText(), Is.EqualTo("Hosting failed: the host could not start"));
            model.Stopped();
            Assert.That(model.Status, Is.EqualTo(HostStatus.Idle));
        }
    }

    public sealed class GameEndpointTests
    {
        [TestCase("192.168.1.20:7777", "192.168.1.20", 7777)]
        [TestCase(" 10.0.0.1:1 ", "10.0.0.1", 1)]
        [TestCase("localhost:7777", "127.0.0.1", 7777)]
        public void AnIpv4AddressAndPortParse(string text, string address, int port)
        {
            Assert.That(GameEndpoint.TryParse(text, out GameEndpoint endpoint, out string problem), Is.True, problem);
            Assert.That(endpoint.Address, Is.EqualTo(address));
            Assert.That((int)endpoint.Port, Is.EqualTo(port));
            Assert.That(endpoint.ToLoopback().ToString(), Is.EqualTo("127.0.0.1:" + port));
        }

        [TestCase("")]
        [TestCase("192.168.1.20")]
        [TestCase("192.168.1.20:0")]
        [TestCase("192.168.1.20:65536")]
        [TestCase("256.1.1.1:7777")]
        [TestCase("1.2.3:7777")]
        [TestCase("example.com:7777")]
        [TestCase("::1:7777")]
        [TestCase("01.2.3.4567:7777")]
        public void AnythingElseIsRefusedWithAProblem(string text)
        {
            Assert.That(GameEndpoint.TryParse(text, out _, out string problem), Is.False);
            Assert.That(problem, Is.Not.Empty);
        }
    }

    public sealed class ClientAppTests
    {
        [Test]
        public void ThePlaceholderIdCountsAsUnconfigured()
        {
            Assert.That(ClientApps.PlaceholderPublicId, Is.EqualTo("dscp_" + new string('0', 32)));
            Assert.That(ClientApps.IsUnconfigured(ClientApps.PlaceholderPublicId), Is.True);
            Assert.That(ClientApps.IsUnconfigured(null), Is.True);
            Assert.That(ClientApps.IsUnconfigured("dscp_0123456789abcdef0123456789abcdef"), Is.False);
            foreach (ClientApp app in new[] { ClientApp.Fleet, ClientApp.Community, ClientApp.SelfHost })
            {
                Assert.That(ClientApps.TryParse(ClientApps.ToName(app), out ClientApp back), Is.True);
                Assert.That(back, Is.EqualTo(app));
            }
        }

        [Test]
        public void ServicesRefuseAnUnconfiguredAppAndAcceptAnOverride()
        {
            var services = new Flows.ClientServices("https://discovery.pingcore.io", "dscp_fleet1", ClientApps.PlaceholderPublicId, ClientApps.PlaceholderPublicId,
                new Flows.ClientServicesOptions { TokenStore = new MemoryTokenStore(), AppPublicIdOverride = "dscp_selfhost1", OverrideApp = ClientApp.SelfHost });
            Assert.That(services.IsConfigured(ClientApp.Fleet), Is.True);
            Assert.That(services.IsConfigured(ClientApp.Community), Is.False);
            Assert.That(services.IsConfigured(ClientApp.SelfHost), Is.True);
            Assert.Throws<InvalidOperationException>(() => services.For(ClientApp.Community));
            services.Dispose();
        }
    }
}
