using System;
using System.Collections.Generic;
using System.Globalization;
using BeaconRush.Session;
using NUnit.Framework;

namespace BeaconRush.Tests.Editor
{
    /// <summary>
    /// The session loop as a table. Each case is a script of inputs and, after every input, the
    /// command it must return and the phase the director must be in. Inputs:
    /// <c>alloc:&lt;id&gt;</c> (default settings), <c>alloc:&lt;id&gt;:&lt;lobbySeconds&gt;:&lt;shutdown|end&gt;</c>,
    /// <c>join:&lt;clientId&gt;</c>, <c>leave:&lt;clientId&gt;</c>, <c>tick:&lt;seconds&gt;</c>, <c>clear</c>,
    /// <c>lost:&lt;otherSeatsHeld&gt;</c> (a claimed joiner never became a player). Expected commands:
    /// <c>-</c> for none, <c>end:&lt;id&gt;:&lt;reason&gt;</c> or <c>shutdown:&lt;id&gt;:&lt;reason&gt;</c>.
    /// </summary>
    public sealed class SessionDirectorTests
    {
        private static IEnumerable<TestCaseData> Scripts()
        {
            yield return Case("an idle game server ignores time",
                "tick:3600", "-", "Lobby/idle");
            yield return Case("an allocation opens a session in the lobby",
                "alloc:a1", "-", "Lobby/a1");
            yield return Case("no player within the default 60 s lobby timeout ends the session",
                "alloc:a1", "-", "Lobby/a1",
                "tick:59.9", "-", "Lobby/a1",
                "tick:0.1", "end:a1:lobby_timeout", "Lobby/idle");
            yield return Case("the context's lobby timeout replaces the default",
                "alloc:a1:10:end", "-", "Lobby/a1",
                "tick:9.99", "-", "Lobby/a1",
                "tick:0.01", "end:a1:lobby_timeout", "Lobby/idle");
            yield return Case("the first player starts the match and stops the lobby timer",
                "alloc:a1", "-", "Lobby/a1",
                "tick:30", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "tick:60", "-", "Match/a1");
            yield return Case("the last player leaving the match ends the session",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "join:2", "-", "Match/a1",
                "leave:2", "-", "Match/a1",
                "leave:1", "end:a1:players_left", "Lobby/idle");
            yield return Case("a full match goes through results and ends complete",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "tick:179", "-", "Match/a1",
                "tick:1", "-", "Results/a1",
                "tick:9", "-", "Results/a1",
                "tick:1", "end:a1:match_complete", "Lobby/idle");
            yield return Case("the last player leaving during results ends the session at once",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "tick:180", "-", "Results/a1",
                "leave:1", "end:a1:players_left", "Lobby/idle");
            yield return Case("endMode shutdown asks for a recycle instead of an end call",
                "alloc:a1:10:shutdown", "-", "Lobby/a1",
                "tick:10", "shutdown:a1:lobby_timeout", "Lobby/idle");
            yield return Case("endMode shutdown also applies to players leaving",
                "alloc:a1:60:shutdown", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "leave:1", "shutdown:a1:players_left", "Lobby/idle");
            yield return Case("the platform clearing the allocation closes the session with no command",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "clear", "-", "Lobby/idle",
                "tick:3600", "-", "Lobby/idle");
            yield return Case("the same allocation again neither resets the lobby timer nor reopens the session",
                "alloc:a1", "-", "Lobby/a1",
                "tick:50", "-", "Lobby/a1",
                "alloc:a1", "-", "Lobby/a1",
                "tick:10", "end:a1:lobby_timeout", "Lobby/idle");
            yield return Case("a different allocation replaces the open session and restarts its timer",
                "alloc:a1", "-", "Lobby/a1",
                "tick:50", "-", "Lobby/a1",
                "alloc:a2", "-", "Lobby/a2",
                "tick:50", "-", "Lobby/a2",
                "tick:10", "end:a2:lobby_timeout", "Lobby/idle");
            yield return Case("after an end the next allocation starts fresh with default settings",
                "alloc:a1:5:shutdown", "-", "Lobby/a1",
                "tick:5", "shutdown:a1:lobby_timeout", "Lobby/idle",
                "alloc:a2", "-", "Lobby/a2",
                "tick:59", "-", "Lobby/a2",
                "tick:1", "end:a2:lobby_timeout", "Lobby/idle");
            yield return Case("a join while idle is not counted, so the next allocation opens in the lobby",
                "join:1", "-", "Lobby/idle",
                "alloc:a1", "-", "Lobby/a1",
                "leave:1", "-", "Lobby/a1",
                "tick:60", "end:a1:lobby_timeout", "Lobby/idle");
            yield return Case("late leaves from the previous session's players neither start nor end the next session",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "join:2", "-", "Match/a1",
                "tick:180", "-", "Results/a1",
                "tick:10", "end:a1:match_complete", "Lobby/idle",
                "alloc:a2", "-", "Lobby/a2",
                "leave:1", "-", "Lobby/a2",
                "leave:2", "-", "Lobby/a2",
                "join:3", "-", "Match/a2",
                "leave:3", "end:a2:players_left", "Lobby/idle");
            yield return Case("a replaced session's players do not carry into the new session",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "alloc:a2", "-", "Lobby/a2",
                "leave:1", "-", "Lobby/a2",
                "tick:60", "end:a2:lobby_timeout", "Lobby/idle");
            yield return Case("a cleared session's players do not end the next session",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "clear", "-", "Lobby/idle",
                "alloc:a2", "-", "Lobby/a2",
                "join:2", "-", "Match/a2",
                "leave:1", "-", "Match/a2",
                "leave:2", "end:a2:players_left", "Lobby/idle");
            yield return Case("a leave in the lobby never ends the session",
                "alloc:a1", "-", "Lobby/a1",
                "leave:9", "-", "Lobby/a1");
            yield return Case("players leaving an idle game server (after an end) change nothing",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "join:2", "-", "Match/a1",
                "tick:180", "-", "Results/a1",
                "tick:10", "end:a1:match_complete", "Lobby/idle",
                "leave:2", "-", "Lobby/idle",
                "leave:1", "-", "Lobby/idle");
            yield return Case("a roster of three keeps the lobby open until the third player is in",
                "roster:a1:3", "-", "Lobby/a1",
                "join:1", "-", "Lobby/a1",
                "join:2", "-", "Lobby/a1",
                "tick:30", "-", "Lobby/a1",
                "join:3", "-", "Match/a1");
            yield return Case("the lobby timeout starts the match with whoever is there",
                "roster:a1:4", "-", "Lobby/a1",
                "join:1", "-", "Lobby/a1",
                "join:2", "-", "Lobby/a1",
                "tick:59.9", "-", "Lobby/a1",
                "tick:0.1", "-", "Match/a1");
            yield return Case("the lobby timeout with a roster but nobody there ends the session",
                "roster:a1:2", "-", "Lobby/a1",
                "tick:60", "end:a1:lobby_timeout", "Lobby/idle");
            yield return Case("a player leaving the roster wait does not end the lobby",
                "roster:a1:2", "-", "Lobby/a1",
                "join:1", "-", "Lobby/a1",
                "leave:1", "-", "Lobby/a1",
                "join:2", "-", "Lobby/a1",
                "join:3", "-", "Match/a1");
            yield return Case("a repeated join does not complete the roster",
                "roster:a1:2", "-", "Lobby/a1",
                "join:1", "-", "Lobby/a1",
                "join:1", "-", "Lobby/a1");
            yield return Case("reaching the score limit ends the match early and results still run their course",
                "alloc:a1", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "tick:20", "-", "Match/a1",
                "score", "-", "Results/a1",
                "tick:9.9", "-", "Results/a1",
                "tick:0.1", "end:a1:match_complete", "Lobby/idle");
            yield return Case("the score limit outside a match changes nothing",
                "score", "-", "Lobby/idle",
                "alloc:a1", "-", "Lobby/a1",
                "score", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "tick:180", "-", "Results/a1",
                "score", "-", "Results/a1");
            yield return Case("a context's match length replaces the three minutes",
                "timed:a1:20:10", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "tick:19.9", "-", "Match/a1",
                "tick:0.1", "-", "Results/a1",
                "tick:10", "end:a1:match_complete", "Lobby/idle");
            yield return Case("a zero results phase is still entered and ends on the next tick",
                "timed:a1:20:0", "-", "Lobby/a1",
                "join:1", "-", "Match/a1",
                "tick:20", "-", "Results/a1",
                "tick:0.01", "end:a1:match_complete", "Lobby/idle");
            yield return Case("a zero or negative tick changes nothing",
                "alloc:a1:1:end", "-", "Lobby/a1",
                "tick:-5", "-", "Lobby/a1",
                "tick:0", "-", "Lobby/a1",
                "tick:1", "end:a1:lobby_timeout", "Lobby/idle");
            yield return Case("a claimed joiner lost with nobody in the lobby ends the self-allocated session at once",
                "alloc:self-1:10:end", "-", "Lobby/self-1",
                "lost:0", "end:self-1:lobby_timeout", "Lobby/idle");
            yield return Case("a claimed joiner lost while another joiner holds a seat changes nothing",
                "alloc:self-1:10:end", "-", "Lobby/self-1",
                "lost:1", "-", "Lobby/self-1",
                "tick:10", "end:self-1:lobby_timeout", "Lobby/idle");
            yield return Case("a claimed joiner lost once a player is in changes nothing",
                "alloc:self-1:10:end", "-", "Lobby/self-1",
                "join:1", "-", "Match/self-1",
                "lost:0", "-", "Match/self-1");
            yield return Case("a claimed joiner lost while idle changes nothing",
                "lost:0", "-", "Lobby/idle");
        }

        [Test]
        public void TheDirectorKnowsWhetherItsSessionHasARoster()
        {
            var director = new SessionDirector();
            Assert.That(director.HasRoster, Is.False, "idle");
            director.OnAllocationReceived("m1", SessionSettings.Default, 2);
            Assert.That(director.HasRoster, Is.True, "a matchmaker allocation's roster");
            director.OnAllocationReceived("b1", SessionSettings.Default, 0);
            Assert.That(director.HasRoster, Is.False, "a backend or self-allocation has none");
            director.OnAllocationReceived("m2", SessionSettings.Default, 1);
            director.OnAllocationCleared();
            Assert.That(director.HasRoster, Is.False, "cleared");
        }

        [TestCaseSource(nameof(Scripts))]
        public void TheSessionLoopFollowsItsTable(string[] script)
        {
            var director = new SessionDirector();
            for (int i = 0; i < script.Length; i += 3)
            {
                SessionCommand command = Apply(director, script[i]);
                Assert.That(Describe(command), Is.EqualTo(script[i + 1]), "command after step " + (i / 3 + 1) + " (" + script[i] + ")");
                string state = director.Phase + "/" + (director.AllocationId ?? "idle");
                Assert.That(state, Is.EqualTo(script[i + 2]), "state after step " + (i / 3 + 1) + " (" + script[i] + ")");
            }
        }

        [Test]
        public void ThePlayerCountIsTheOpenSessionsDistinctPlayers()
        {
            var director = new SessionDirector();
            director.OnPlayerLeft(1);
            director.OnPlayerJoined(1);
            Assert.That(director.Players, Is.EqualTo(0), "idle: nothing counts");

            director.OnAllocationReceived("a1", SessionSettings.Default);
            director.OnPlayerJoined(1);
            director.OnPlayerJoined(1);
            director.OnPlayerJoined(2);
            Assert.That(director.Players, Is.EqualTo(2), "a repeated id counts once");
            Assert.That(director.IsSessionPlayer(1), Is.True);
            director.OnPlayerLeft(7);
            Assert.That(director.Players, Is.EqualTo(2), "a stranger's leave changes nothing");
            director.OnPlayerLeft(1);
            director.OnPlayerLeft(1);
            Assert.That(director.Players, Is.EqualTo(1));
            Assert.That(director.IsSessionPlayer(1), Is.False);

            director.OnAllocationCleared();
            Assert.That(director.Players, Is.EqualTo(0), "a closed session has no players");
            Assert.That(director.IsSessionPlayer(2), Is.False);
        }

        [Test]
        public void EveryPhaseEntryCarriesItsCauseAndBumpsTheVersion()
        {
            var director = new SessionDirector();
            var seen = new List<string>();
            long version = director.PhaseVersion;

            void Record()
            {
                if (director.PhaseVersion != version)
                {
                    version = director.PhaseVersion;
                    seen.Add(director.HasSession ? director.Phase + ":" + SessionEndReasons.ToWire(director.Cause) : "idle");
                }
            }

            director.OnAllocationReceived("a1", SessionSettings.Default, 2);
            Record();
            Assert.That(director.ExpectedPlayers, Is.EqualTo(2));
            director.OnPlayerJoined(1);
            Record();
            director.OnPlayerJoined(2);
            Record();
            director.OnScoreLimitReached();
            Record();
            director.Tick(SessionSettings.DefaultResultsDuration);
            Record();
            director.OnAllocationReceived("a2", SessionSettings.Default, 3);
            Record();
            director.OnPlayerJoined(5);
            Record();
            director.Tick(SessionSettings.DefaultLobbyTimeout);
            Record();
            director.Tick(SessionSettings.DefaultMatchDuration);
            Record();

            Assert.That(seen, Is.EqualTo(new[]
            {
                "Lobby:allocation", "Match:roster_complete", "Results:score_limit", "idle",
                "Lobby:allocation", "Match:lobby_timeout", "Results:time_up",
            }));
            Assert.That(director.ExpectedPlayers, Is.EqualTo(3));
        }

        [Test]
        public void AnAllocationWithoutARosterWaitsForOnePlayer()
        {
            var director = new SessionDirector();
            director.OnAllocationReceived("a1", SessionSettings.Default, 0);
            Assert.That(director.ExpectedPlayers, Is.EqualTo(1));
            director.OnAllocationReceived("a2", SessionSettings.Default, -4);
            Assert.That(director.ExpectedPlayers, Is.EqualTo(1));
            director.OnAllocationCleared();
            Assert.That(director.ExpectedPlayers, Is.EqualTo(0), "idle waits for no one");
        }

        [Test]
        public void TimeLeftCountsDownTheCurrentPhase()
        {
            var director = new SessionDirector();
            Assert.That(director.TimeLeft, Is.EqualTo(TimeSpan.Zero), "idle");
            director.OnAllocationReceived("a1", SessionSettings.Default.WithMatchDuration(TimeSpan.FromSeconds(30)));
            director.Tick(TimeSpan.FromSeconds(15));
            Assert.That(director.TimeLeft, Is.EqualTo(TimeSpan.FromSeconds(45)), "lobby");
            director.OnPlayerJoined(1);
            director.Tick(TimeSpan.FromSeconds(10));
            Assert.That(director.TimeLeft, Is.EqualTo(TimeSpan.FromSeconds(20)), "match");
        }

        [Test]
        public void AnAllocationWithoutAnIdIsRefused()
        {
            var director = new SessionDirector();
            Assert.Throws<ArgumentException>(() => director.OnAllocationReceived(null, SessionSettings.Default));
            Assert.Throws<ArgumentException>(() => director.OnAllocationReceived(string.Empty, SessionSettings.Default));
            Assert.That(director.HasSession, Is.False);
        }

        [Test]
        public void TheTableHarnessCanFail()
        {
            // The harness itself: a wrong expectation must be reported, or every case above proves nothing.
            var director = new SessionDirector();
            director.OnAllocationReceived("a1", SessionSettings.Default);
            Assert.That(Describe(director.Tick(TimeSpan.FromSeconds(60))), Is.Not.EqualTo("-"));
        }

        private static TestCaseData Case(string name, params string[] script) =>
            new TestCaseData((object)script).SetName(name.Replace(".", "_"));

        private static SessionCommand Apply(SessionDirector director, string step)
        {
            string[] parts = step.Split(':');
            switch (parts[0])
            {
                case "alloc":
                    SessionSettings settings = SessionSettings.Default;
                    if (parts.Length == 4)
                    {
                        settings = settings.WithLobbyTimeout(TimeSpan.FromSeconds(double.Parse(parts[2], CultureInfo.InvariantCulture)))
                            .WithEndMode(parts[3] == "shutdown" ? SessionEndMode.Shutdown : SessionEndMode.EndSession);
                    }

                    return director.OnAllocationReceived(parts[1], settings);
                case "roster":
                    return director.OnAllocationReceived(parts[1], SessionSettings.Default, int.Parse(parts[2], CultureInfo.InvariantCulture));
                case "timed":
                    return director.OnAllocationReceived(parts[1], SessionSettings.Default
                        .WithMatchDuration(TimeSpan.FromSeconds(double.Parse(parts[2], CultureInfo.InvariantCulture)))
                        .WithResultsDuration(TimeSpan.FromSeconds(double.Parse(parts[3], CultureInfo.InvariantCulture))));
                case "score":
                    return director.OnScoreLimitReached();
                case "join":
                    return director.OnPlayerJoined(ulong.Parse(parts[1], CultureInfo.InvariantCulture));
                case "leave":
                    return director.OnPlayerLeft(ulong.Parse(parts[1], CultureInfo.InvariantCulture));
                case "clear":
                    return director.OnAllocationCleared();
                case "lost":
                    return director.OnClaimedJoinLost(int.Parse(parts[1], CultureInfo.InvariantCulture));
                case "tick":
                    return director.Tick(TimeSpan.FromSeconds(double.Parse(parts[1], CultureInfo.InvariantCulture)));
                default:
                    throw new ArgumentException("unknown step " + step);
            }
        }

        private static string Describe(SessionCommand command)
        {
            switch (command.Kind)
            {
                case SessionCommandKind.None:
                    return "-";
                case SessionCommandKind.EndSession:
                    return "end:" + command.AllocationId + ":" + SessionEndReasons.ToWire(command.Reason);
                case SessionCommandKind.Shutdown:
                    return "shutdown:" + command.AllocationId + ":" + SessionEndReasons.ToWire(command.Reason);
                default:
                    return "?";
            }
        }
    }
}
