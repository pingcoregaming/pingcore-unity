using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core.Discovery;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>The list query's pure encoding (exact strings) and the list call's paging.</summary>
    public sealed class ServerListQueryTests
    {
        [Test]
        public void AnEmptyQueryIsAnEmptyString()
        {
            Assert.That(new ServerListQuery().ToQueryString(), Is.EqualTo(string.Empty));
        }

        [Test]
        public void EveryParameterRendersInTheFixedOrder()
        {
            string q = new ServerListQuery()
                .MaxLatency(80)
                .WithLatency(new Dictionary<string, int> { ["us-east-nyc"] = 95, ["eu-west-ams"] = 20 })
                .SortBy(ServerSort.Players, descending: true)
                .Meta("proto", 2L)
                .MetaWhere("xp", MetaOp.Gt, 1.5)
                .MetaWhere("map", MetaOp.In, new[] { "DustII", "Airport" })
                .HasSlots(true)
                .Version("1.2.0")
                .Search("coop night")
                .Page(20, 40)
                .ToQueryString();
            Assert.That(q, Is.EqualTo("limit=20&offset=40&search=coop%20night&version=1.2.0&hasSlots=true&meta.proto=2&meta.xp[gt]=1.5&meta.map[in]=DustII%2CAirport&sort=-players&latency.eu-west-ams=20&latency.us-east-nyc=95&maxLatencyMs=80"));
        }

        [TestCase(ServerSort.Players, false, "sort=players")]
        [TestCase(ServerSort.Name, false, "sort=name")]
        [TestCase(ServerSort.UpdatedAt, true, "sort=-updatedAt")]
        [TestCase(ServerSort.Latency, false, "sort=latency")]
        public void SortKeysUseTheWireSpelling(ServerSort sort, bool descending, string expected)
        {
            Assert.That(new ServerListQuery().SortBy(sort, descending).ToQueryString(), Is.EqualTo(expected));
        }

        [Test]
        public void MetaSortExactMatchesBooleansAndEveryOperatorEncode()
        {
            Assert.That(new ServerListQuery().SortByMeta("xp", descending: true).ToQueryString(), Is.EqualTo("sort=-meta.xp"));
            Assert.That(new ServerListQuery().Meta("pvp", true).Meta("map", "Coast Line").ToQueryString(), Is.EqualTo("meta.pvp=true&meta.map=Coast%20Line"));
            Assert.That(
                new ServerListQuery().MetaWhere("a", MetaOp.Eq, "x").MetaWhere("a", MetaOp.Ne, 3L).MetaWhere("a", MetaOp.Ge, 1L).MetaWhere("a", MetaOp.Lt, 9L).MetaWhere("a", MetaOp.Le, 8L).MetaWhere("n", MetaOp.Contains, "co&op").ToQueryString(),
                Is.EqualTo("meta.a[eq]=x&meta.a[ne]=3&meta.a[ge]=1&meta.a[lt]=9&meta.a[le]=8&meta.n[contains]=co%26op"));
        }

        [Test]
        public void ACeilingWithoutALatencyMapIsNotSentBecauseDiscoveryIgnoresIt()
        {
            Assert.That(new ServerListQuery().MaxLatency(80).ToQueryString(), Is.EqualTo(string.Empty));
        }

        [Test]
        public void MoreThan32LatencyEntriesKeepTheLowest32()
        {
            var map = new Dictionary<string, int>();
            for (int i = 0; i < 40; i++)
            {
                map["loc" + i.ToString("D2")] = 100 - i;
            }

            string q = new ServerListQuery().WithLatency(map).ToQueryString();
            Assert.That(q.Split('&').Length, Is.EqualTo(32));
            Assert.That(q, Does.Contain("latency.loc39=61"), "the lowest is kept");
            Assert.That(q, Does.Not.Contain("latency.loc00="), "the highest is dropped");
            Assert.That(q, Does.Not.Contain("latency.loc07="));
            Assert.That(q, Does.Contain("latency.loc08=92"));
        }

        [Test]
        public void InvalidArgumentsThrowBecauseTheyAreProgrammingErrors()
        {
            Assert.Throws<ArgumentException>(() => new ServerListQuery().Meta("bad key", "x"));
            Assert.Throws<ArgumentException>(() => new ServerListQuery().Meta("k", string.Empty));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerListQuery().Page(501));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerListQuery().Page(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerListQuery().Page(10, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerListQuery().MaxLatency(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerListQuery().Meta("x", double.NaN));
            Assert.Throws<ArgumentException>(() => new ServerListQuery().WithLatency(new Dictionary<string, int> { ["bad id"] = 1 }));
            Assert.Throws<ArgumentException>(() => new ServerListQuery().MetaWhere("m", MetaOp.In, new[] { "a,b" }));
        }

        [Test]
        public void CloneIsIndependent()
        {
            ServerListQuery original = new ServerListQuery().HasSlots(true).Page(10);
            ServerListQuery copy = original.Clone().Page(10, 10).Meta("m", "x");
            Assert.That(original.ToQueryString(), Is.EqualTo("limit=10&offset=0&hasSlots=true"));
            Assert.That(copy.ToQueryString(), Is.EqualTo("limit=10&offset=10&hasSlots=true&meta.m=x"));
        }

        [Test]
        public async Task TheListCallSendsTheQueryUnauthenticatedAndPagesOn()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(200, "{\"error\":false,\"servers\":[{\"serverId\":\"agent-a-1\",\"name\":\"A\",\"ip\":\"203.0.113.10\",\"port\":27015,\"players\":3,\"maxPlayers\":8,\"version\":\"1.0.0\",\"meta\":null}],\"totalServers\":3,\"returned\":1,\"limit\":1,\"offset\":0}"));
                DiscoveryResult<ServerPage> page = await h.Client.ListServersAsync(new ServerListQuery().HasSlots(true).Page(1), CancellationToken.None);

                Assert.That(page.IsOk, Is.True, page.ToString());
                var sent = h.Http.To("GET", ClientHarness.ServersPath);
                Assert.That(sent.Count, Is.EqualTo(1));
                Assert.That(sent[0].Query, Is.EqualTo("limit=1&offset=0&hasSlots=true"));
                Assert.That(sent[0].Authorization, Is.Null, "the list needs no player token");
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(0));
                Assert.That(page.Value.Servers[0].ServerId, Is.EqualTo("agent-a-1"));
                Assert.That(page.Value.HasMore, Is.True);
                Assert.That(page.Value.Next.ToQueryString(), Is.EqualTo("limit=1&offset=1&hasSlots=true"));
            }
        }

        [Test]
        public async Task TheLastPageHasNoNext()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(200, "{\"error\":false,\"servers\":[],\"totalServers\":0,\"returned\":0,\"limit\":100,\"offset\":0}"));
                DiscoveryResult<ServerPage> page = await h.Client.ListServersAsync(null, CancellationToken.None);
                Assert.That(page.Value.HasMore, Is.False);
                Assert.That(page.Value.Next, Is.Null);
                Assert.That(h.Http.Requests[0].Query, Is.EqualTo(string.Empty));
            }
        }

        [Test]
        public async Task LocationsAreCachedForFiveMinutes()
        {
            using (var h = new ClientHarness())
            {
                const string body = "{\"error\":false,\"locations\":[{\"id\":\"eu-west-ams\",\"name\":\"Amsterdam\",\"pingUrl\":\"wss://ping.test/ams\",\"enabled\":true}],\"returned\":1}";
                h.Http.On("GET", ClientHarness.LocationsPath, Step.Json(200, body));
                Assert.That((await h.Client.GetLocationsAsync(CancellationToken.None)).Value[0].Id, Is.EqualTo("eu-west-ams"));
                h.Scheduler.Advance(TimeSpan.FromMinutes(4));
                Assert.That((await h.Client.GetLocationsAsync(CancellationToken.None)).IsOk, Is.True);
                Assert.That(h.Http.Count("GET", ClientHarness.LocationsPath), Is.EqualTo(1), "cached");
                h.Scheduler.Advance(TimeSpan.FromMinutes(1));
                Assert.That((await h.Client.GetLocationsAsync(CancellationToken.None)).IsOk, Is.True);
                Assert.That(h.Http.Count("GET", ClientHarness.LocationsPath), Is.EqualTo(2), "refetched at 5 min");
            }
        }
    }
}
