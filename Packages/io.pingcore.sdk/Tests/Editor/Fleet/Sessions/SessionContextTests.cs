using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Handshake;
using PingCore.Fleet.Sessions;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Tests.Editor.Sessions
{
    /// <summary>
    /// <see cref="MatchContext"/> and <see cref="BackfillContext"/> against the context shapes Discovery's
    /// matchmaker delivers. The JSON is transcribed from Discovery 1.5.1's matchmaker test suite, which pins both
    /// frames' context exactly, plus the supervisor's backfill list fixture
    /// (<c>contracts/supervisor/fixtures/backfills.json</c>).
    /// </summary>
    public sealed class SessionContextTests
    {
        /// <summary>A formed match's allocation context.</summary>
        internal const string FormedMatch =
            "{\"matchmaker\":true,\"queue\":\"form\",\"sessionSize\":1,\"players\":1,\"relaxed\":false,\"backfill\":false,\"location\":null," +
            "\"roster\":[{\"ticketId\":\"f\",\"partySize\":1,\"playerId\":null,\"attributes\":null,\"context\":{\"partyName\":\"solo\"}}]}";

        /// <summary>A backfill frame's context.</summary>
        internal const string BackfillFrame =
            "{\"matchmaker\":true,\"backfill\":true,\"queue\":\"join\",\"sessionSize\":8,\"players\":1,\"location\":null,\"sessionId\":\"sess-9\"," +
            "\"roster\":[{\"ticketId\":\"j\",\"partySize\":1,\"playerId\":\"steam:7656\",\"attributes\":null,\"context\":null}]}";

        [Test]
        public void AFormedMatchContextParsesEveryField()
        {
            var info = new AllocationInfo("alloc-1", JObject.Parse(FormedMatch), DateTimeOffset.UnixEpoch);
            MatchContext match = MatchContext.Parse(info);
            Assert.That(match.AllocationId, Is.EqualTo("alloc-1"));
            Assert.That(match.IsMatchmaker, Is.True);
            Assert.That(match.Queue, Is.EqualTo("form"));
            Assert.That(match.SessionSize, Is.EqualTo(1));
            Assert.That(match.Players, Is.EqualTo(1));
            Assert.That(match.Relaxed, Is.False);
            Assert.That(match.Backfill, Is.False);
            Assert.That(match.Location, Is.Null);
            Assert.That(match.Roster, Has.Count.EqualTo(1));
            RosterEntry entry = match.Roster[0];
            Assert.That(entry.TicketId, Is.EqualTo("f"));
            Assert.That(entry.PartySize, Is.EqualTo(1));
            Assert.That(entry.PlayerId, Is.Null, "a backend ticket's roster player is null");
            Assert.That(entry.Attributes, Is.Null);
            Assert.That((string)entry.Context["partyName"], Is.EqualTo("solo"));
            Assert.That(match.RosterPlayers, Is.EqualTo(1));
            Assert.That(match.Raw["roster"], Is.Not.Null, "the raw context stays available for the game's own keys");
        }

        [Test]
        public void ARelaxedFourPlayerMatchWithPartiesSumsTheRoster()
        {
            const string context = "{\"matchmaker\":true,\"queue\":\"rush-p2\",\"sessionSize\":4,\"players\":3,\"relaxed\":true,\"backfill\":false," +
                "\"location\":\"ams\",\"roster\":[{\"ticketId\":\"t-a\",\"partySize\":2,\"playerId\":\"anon:a\",\"attributes\":{\"skill\":1200,\"ping\":31.5},\"context\":null}," +
                "{\"ticketId\":\"t-b\",\"partySize\":1,\"playerId\":\"anon:b\",\"attributes\":null,\"context\":null}]," +
                "\"tuning\":{\"matchSeconds\":20}}";
            MatchContext match = MatchContext.Parse(new AllocationInfo("alloc-2", JObject.Parse(context), DateTimeOffset.UnixEpoch));
            Assert.That(match.Relaxed, Is.True);
            Assert.That(match.Location, Is.EqualTo("ams"));
            Assert.That(match.RosterPlayers, Is.EqualTo(3));
            Assert.That(match.Roster[0].Attributes["skill"], Is.EqualTo(1200));
            Assert.That(match.Roster[0].Attributes["ping"], Is.EqualTo(31.5));
            Assert.That((int)match.Raw["tuning"]["matchSeconds"], Is.EqualTo(20));
        }

        [Test]
        public void ABackendAllocationContextIsNotAMatchAndHasAnEmptyRoster()
        {
            MatchContext match = MatchContext.Parse(new AllocationInfo("self-1", JObject.Parse("{\"mode\":\"duel\"}"), DateTimeOffset.UnixEpoch));
            Assert.That(match.IsMatchmaker, Is.False);
            Assert.That(match.Roster, Is.Empty);
            Assert.That(match.RosterPlayers, Is.EqualTo(0));
            Assert.That(match.Queue, Is.Null);
            Assert.That(MatchContext.Parse((AllocationInfo)null), Is.Null);
            Assert.That(MatchContext.Parse("a", null).Raw, Is.Empty);
        }

        [Test]
        public void OnlyAnAllocationWithNoRosterAndNoMatchmakerIsRosterless()
        {
            Assert.That(MatchContext.Parse("a", JObject.Parse(FormedMatch)).HasRoster, Is.True, "a formed match");
            Assert.That(MatchContext.Parse("self-123", JObject.Parse("{}")).HasRoster, Is.False, "a self-allocation's empty context");
            Assert.That(MatchContext.Parse("a", null).HasRoster, Is.False, "no context at all");
            Assert.That(MatchContext.Parse("a", JObject.Parse("{\"mode\":\"duel\"}")).HasRoster, Is.False, "a backend context");
            Assert.That(MatchContext.Parse("a", JObject.Parse("{\"roster\":null}")).HasRoster, Is.False, "a null roster");
            Assert.That(MatchContext.Parse("a", JObject.Parse("{\"roster\":[]}")).HasRoster, Is.False, "an empty roster");
            Assert.That(MatchContext.Parse("a", JObject.Parse("{\"roster\":[{\"ticketId\":\"\",\"partySize\":1}]}")).HasRoster, Is.True,
                "a roster with no usable entry fails closed");
            Assert.That(MatchContext.Parse("a", JObject.Parse("{\"roster\":{\"ticketId\":\"x\"}}")).HasRoster, Is.True, "a roster that is not an array fails closed");
            Assert.That(MatchContext.Parse("a", JObject.Parse("{\"matchmaker\":true,\"roster\":[]}")).HasRoster, Is.True, "a matchmaker allocation is never rosterless");

            // A context annotation that did not parse leaves an empty context; it must not read as rosterless.
            // Mutation: drop contextInvalid from HasRoster and the first assertion fails.
            var unreadable = new AllocationInfo("a", new JObject(), DateTimeOffset.UnixEpoch, contextInvalid: true);
            Assert.That(MatchContext.Parse(unreadable).HasRoster, Is.True, "an unreadable context fails closed");
            Assert.That(MatchContext.Parse(unreadable).ContextInvalid, Is.True);
            Assert.That(MatchContext.Parse(new AllocationInfo("a", new JObject(), DateTimeOffset.UnixEpoch, contextInvalid: false)).HasRoster, Is.False, "control: the same empty context that parsed is rosterless");
        }

        [Test]
        public void MistypedFieldsReadAsAbsentAndRosterEntriesThatAdmitNoOneAreSkipped()
        {
            const string context = "{\"matchmaker\":\"yes\",\"queue\":7,\"sessionSize\":\"4\",\"players\":2.5,\"roster\":[" +
                "{\"ticketId\":\"ok\",\"partySize\":2,\"playerId\":\"anon:a\"}," +
                "{\"ticketId\":\"\",\"partySize\":1}," +
                "{\"ticketId\":\"zero\",\"partySize\":0}," +
                "{\"ticketId\":\"text\",\"partySize\":\"2\"}," +
                "{\"ticketId\":\"badplayer\",\"partySize\":1,\"playerId\":5}," +
                "{\"partySize\":1}, 7, null]}";
            MatchContext match = MatchContext.Parse("a", JObject.Parse(context));
            Assert.That(match.IsMatchmaker, Is.False);
            Assert.That(match.Queue, Is.Null);
            Assert.That(match.SessionSize, Is.Null);
            Assert.That(match.Players, Is.Null);
            Assert.That(match.Roster, Has.Count.EqualTo(1));
            Assert.That(match.Roster[0].TicketId, Is.EqualTo("ok"));
            Assert.That(MatchContext.Parse("a", JObject.Parse("{\"roster\":{\"ticketId\":\"x\"}}")).Roster, Is.Empty, "a roster that is not an array");
        }

        [Test]
        public void ABackfillFrameParsesEveryFieldAndItsSessionId()
        {
            var view = new BackfillView
            {
                AllocationId = "bf-1",
                Context = JObject.Parse(BackfillFrame),
                Claims = JObject.Parse("{\"players\":1}"),
                DeliveredAt = 1234,
            };
            BackfillContext backfill = BackfillContext.Parse(view);
            Assert.That(backfill.AllocationId, Is.EqualTo("bf-1"));
            Assert.That(backfill.SessionId, Is.EqualTo("sess-9"), "no top-level sessionId: the context's is used");
            Assert.That(backfill.IsMatchmaker, Is.True);
            Assert.That(backfill.Queue, Is.EqualTo("join"));
            Assert.That(backfill.SessionSize, Is.EqualTo(8));
            Assert.That(backfill.Players, Is.EqualTo(1));
            Assert.That(backfill.Location, Is.Null);
            Assert.That(backfill.Roster[0].PlayerId, Is.EqualTo("steam:7656"));
            Assert.That(backfill.RosterPlayers, Is.EqualTo(1));
            Assert.That(backfill.DeliveredAt, Is.EqualTo(1234));
            Assert.That((int)backfill.Claims["players"], Is.EqualTo(1));
        }

        [Test]
        public void TheSupervisorsResolvedSessionIdWinsOverTheContexts()
        {
            BackfillList list = JsonConvert.DeserializeObject<BackfillList>(
                (string)JObject.Parse(System.IO.File.ReadAllText(FixturePath()))["payload"].ToString(Formatting.None), PingCoreJson.Settings);
            BackfillView view = list.Backfills[0];
            view.Context["sessionId"] = "stale";
            BackfillContext backfill = BackfillContext.Parse(view);
            Assert.That(backfill.SessionId, Is.EqualTo("match-1"));
            Assert.That(backfill.Roster[0].PartySize, Is.EqualTo(2));
            Assert.That(backfill.RosterPlayers, Is.EqualTo(2));
            Assert.That(BackfillContext.Parse(new BackfillView()), Is.Null, "a backfill without an id");
            Assert.That(BackfillContext.Parse(null), Is.Null);
        }

        [Test]
        public void RosterEntriesShowOnlyTheirTicketRef()
        {
            RosterEntry entry = MatchContext.Parse("a", JObject.Parse(FormedMatch)).Roster[0];
            Assert.That(entry.TicketRef, Does.Match("^[0-9a-f]{12}$"));
            Assert.That(entry.ToString(), Does.Contain(entry.TicketRef).And.Contain("(backend)"));
            Assert.That(entry.HasTicket("f"), Is.True);
            Assert.That(entry.HasTicket("F"), Is.False);
        }

        private static string FixturePath()
        {
            string package = System.IO.Path.GetFullPath(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(IFleetSdk).Assembly).resolvedPath);
            return System.IO.Path.Combine(package, "..", "..", "contracts", "supervisor", "fixtures", "backfills.json");
        }
    }
}
