using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core.Discovery;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// The missing-infrastructure check (<see cref="InfrastructureCheck"/>) as a table: each state from the pinned Discovery
    /// fixtures and the spec's documented answers, through the real client and its call pipeline, and every answer the spec
    /// does not document classified as a Discovery error, never as "no game servers". Then the Editor-only detail hook:
    /// asked only for the states the workspace can explain, never waited on for longer than its timeout, and never able to
    /// put a token on screen.
    /// </summary>
    public sealed class InfrastructureCheckTests
    {
        private InfrastructureEditorHook.Answerer saved;

        [SetUp]
        public void SaveTheRegisteredAnswerer()
        {
            // The Editor plugin registers its own answerer when the Editor loads; these tests replace it and put it back.
            saved = InfrastructureEditorHook.Current;
            InfrastructureEditorHook.Register(null);
        }

        [TearDown]
        public void RestoreTheRegisteredAnswerer() => InfrastructureEditorHook.Register(saved);

        [Test]
        public void EachStateHasTheSpecsExactMessage()
        {
            Assert.That(InfrastructureCheck.MessageFor(InfrastructureState.Ok), Is.Null);
            Assert.That(InfrastructureCheck.MessageFor(InfrastructureState.NoAppId), Is.EqualTo("Not connected to PingCore yet. Set up the game and a fleet in your PingCore panel, then open Window > PingCore, sign in and pick the fleet."));
            Assert.That(InfrastructureCheck.MessageFor(InfrastructureState.AppUnknown), Is.EqualTo("PingCore does not recognise this game's app id. Pick the fleet again in Window > PingCore."));
            Assert.That(InfrastructureCheck.MessageFor(InfrastructureState.NoGameServers), Is.EqualTo("No game servers are running for this game. Add a deployment to your fleet in the panel."));
            Assert.That(InfrastructureCheck.MessageFor(InfrastructureState.Unreachable), Is.EqualTo("Cannot reach PingCore Discovery. Check your connection."));
            Assert.That(InfrastructureCheck.MessageFor(InfrastructureState.DiscoveryError, null), Is.EqualTo("PingCore Discovery could not answer the check (no answer). Try again in a moment."));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public async Task ABlankAppIdIsNoAppIdAndSendsNothing(string appId)
        {
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                InfrastructureReport report = await InfrastructureCheck.RunAsync(appId, null, h.Scheduler, CancellationToken.None);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.NoAppId));
                Assert.That(report.Message, Is.EqualTo(InfrastructureCheck.NoAppIdMessage));
                Assert.That(report.Answer, Is.Null);
                Assert.That(h.Http.Requests, Is.Empty, "no app id, no call");
            }
        }

        [TestCase("not-an-app-id")]
        [TestCase("dscp_")]
        [TestCase("dscp_has a space")]
        public async Task AnIdThatIsNotAPublicIdIsAppUnknownAndSendsNothing(string appId)
        {
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                InfrastructureReport report = await InfrastructureCheck.RunAsync(appId, null, h.Scheduler, CancellationToken.None);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.AppUnknown));
                Assert.That(h.Http.Requests, Is.Empty);
            }
        }

        [Test]
        public async Task TheServerListFixtureIsOkAndTheCheckAsksForOnePage()
        {
            JObject payload = Fixture("server-list.json");
            int total = payload.Value<int>("totalServers");
            Assert.That(total, Is.GreaterThan(0), "the fixture lists game servers");
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(200, payload));
                InfrastructureReport report = await Run(h);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.Ok), report.Answer?.ToString());
                Assert.That(report.IsOk, Is.True);
                Assert.That(report.Message, Is.Null);
                Assert.That(report.ToString(), Is.Empty);
                Assert.That(report.TotalServers, Is.EqualTo(total));
                var sent = h.Http.To("GET", ClientHarness.ServersPath);
                Assert.That(sent.Count, Is.EqualTo(1));
                Assert.That(sent[0].Query, Is.EqualTo("limit=1&offset=0"), "one page of one: totalServers counts the rest");
                Assert.That(sent[0].Authorization, Is.Null, "the list needs no player token");
            }
        }

        [Test]
        public async Task TheServerListFixtureWithNoGameServerIsNoGameServers()
        {
            JObject payload = Fixture("server-list.json");
            payload["servers"] = new JArray();
            payload["totalServers"] = 0;
            payload["returned"] = 0;
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(200, payload));
                InfrastructureReport report = await Run(h);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.NoGameServers));
                Assert.That(report.Message, Is.EqualTo(InfrastructureCheck.NoGameServersMessage));
            }
        }

        [Test]
        public async Task TheSpecsUnknownAppAnswerIsAppUnknown()
        {
            // The pinned spec: the list's only 404 is UnknownApp, a bare ErrorEnvelope with no reason, the same for an
            // unknown public id and a disabled app. The check reads that status and the absent reason, never the text.
            JObject spec = JObject.Parse(File.ReadAllText(Path.Combine(DiscoveryContracts, "openapi.json")));
            JObject responses = (JObject)spec["paths"]["/v1/apps/{publicId}/servers"]["get"]["responses"];
            Assert.That(responses["404"]?["$ref"]?.ToString(), Is.EqualTo("#/components/responses/UnknownApp"));
            JToken unknownApp = spec["components"]["responses"]["UnknownApp"];
            Assert.That(unknownApp["description"].ToString(), Does.Contain("DISABLED app"), "the spec says a disabled app answers the same 404");
            Assert.That(unknownApp["content"]["application/json"]["schema"]["$ref"].ToString(), Is.EqualTo("#/components/schemas/ErrorEnvelope"));
            Assert.That(((JObject)spec["components"]["schemas"]["ErrorEnvelope"]["properties"]).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "error", "message" }), "no reason on UnknownApp");

            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Error(404, "any text at all"));
                InfrastructureReport report = await Run(h);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.AppUnknown));
                Assert.That(report.Message, Is.EqualTo(InfrastructureCheck.AppUnknownMessage));
            }
        }

        [Test]
        public async Task ATransportErrorIsUnreachable()
        {
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Unreachable());
                InfrastructureReport report = await Run(h);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.Unreachable));
                Assert.That(report.Message, Is.EqualTo(InfrastructureCheck.UnreachableMessage));
            }
        }

        [Test]
        public async Task TheDegradedFixtureIsADiscoveryErrorThatNamesTheOutcome()
        {
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(503, Fixture("error.degraded.json")));
                InfrastructureReport report = await Run(h);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.DiscoveryError));
                Assert.That(report.Message, Is.EqualTo("PingCore Discovery could not answer the check (Degraded 503). Try again in a moment."));
            }
        }

        // Answers the spec does not document for this route, or that say nothing about game servers: never "no game servers".
        [TestCase(404, "{\"error\":true,\"message\":\"gone\",\"reason\":\"app_paused\"}", Description = "a 404 with a reason this SDK does not know")]
        [TestCase(418, "{\"error\":true,\"message\":\"teapot\"}", Description = "an unknown status")]
        [TestCase(500, "{\"error\":true,\"message\":\"boom\"}", Description = "a server error")]
        [TestCase(429, "{\"error\":true,\"message\":\"slow down\"}", Description = "rate limited")]
        [TestCase(403, "{\"error\":true,\"message\":\"forbidden\"}", Description = "forbidden")]
        [TestCase(200, "not json at all", Description = "a 200 whose body does not parse")]
        [TestCase(200, "{\"error\":true,\"message\":\"odd\"}", Description = "a 200 whose envelope says error")]
        public async Task AnyOtherAnswerIsADiscoveryErrorNeverNoGameServers(int status, string body)
        {
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(status, body));
                InfrastructureReport report = await Run(h);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.DiscoveryError), report.Answer?.ToString());
                Assert.That(report.Message, Does.StartWith(InfrastructureCheck.DiscoveryErrorMessage));
            }
        }

        [Test]
        public async Task ACancelledCheckIsADiscoveryError()
        {
            using (var h = new ClientHarness(maxAttempts: 1))
            using (var cancel = new CancellationTokenSource())
            {
                var gate = new TaskCompletionSource<bool>();
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(200, Fixture("server-list.json")).HeldBy(gate));
                Task<InfrastructureReport> running = InfrastructureCheck.RunAsync(ClientHarness.AppId, h.Client, h.Scheduler, cancel.Token);
                cancel.Cancel();
                InfrastructureReport report = await running;
                Assert.That(report.State, Is.EqualTo(InfrastructureState.DiscoveryError));
            }
        }

        [Test]
        public void ClassifyIsPureAndReadsNoMessageText()
        {
            var ok = new DiscoveryCallResult(DiscoveryOutcome.Ok, 200, null, null, null, null, null);
            var notFound = new DiscoveryCallResult(DiscoveryOutcome.NotFound, 404, null, "No game servers here", null, null, null);
            Assert.That(InfrastructureCheck.Classify(ClientHarness.AppId, ok, 3), Is.EqualTo(InfrastructureState.Ok));
            Assert.That(InfrastructureCheck.Classify(ClientHarness.AppId, ok, 0), Is.EqualTo(InfrastructureState.NoGameServers));
            Assert.That(InfrastructureCheck.Classify(ClientHarness.AppId, notFound, 0), Is.EqualTo(InfrastructureState.AppUnknown), "the text says no game servers; the status decides");
            Assert.That(InfrastructureCheck.Classify(ClientHarness.AppId, null, 0), Is.EqualTo(InfrastructureState.DiscoveryError));
            Assert.That(InfrastructureCheck.Classify(string.Empty, ok, 3), Is.EqualTo(InfrastructureState.NoAppId), "no app id wins over any answer");
        }

        [Test]
        public async Task TheClientMustBeForTheCheckedApp()
        {
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                Assert.That(await Thrown(InfrastructureCheck.RunAsync(ClientHarness.AppId, null, h.Scheduler, CancellationToken.None)), Is.TypeOf<ArgumentNullException>());
                Assert.That(await Thrown(InfrastructureCheck.RunAsync("dscp_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", h.Client, h.Scheduler, CancellationToken.None)), Is.TypeOf<ArgumentException>());
                Assert.That(h.Http.Requests, Is.Empty);
            }
        }

        private static async Task<Exception> Thrown(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        // ---- the Editor-only detail ----------------------------------------------------------------------------------

        [Test]
        public async Task TheEditorDetailIsAskedForTheStatesTheWorkspaceExplainsAndShownUnderTheMessage()
        {
            InfrastructureQuestion asked = null;
            InfrastructureEditorHook.Register((q, ct) =>
            {
                asked = q;
                return Task.FromResult("  Fleet Beacon Rush has no deployment.  ");
            });
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(200, "{\"error\":false,\"servers\":[],\"totalServers\":0,\"returned\":0,\"limit\":1,\"offset\":0}"));
                InfrastructureReport report = await Run(h);
                Assert.That(report.State, Is.EqualTo(InfrastructureState.NoGameServers));
                Assert.That(report.EditorDetail, Is.EqualTo("Fleet Beacon Rush has no deployment."));
                Assert.That(report.ToString(), Is.EqualTo(InfrastructureCheck.NoGameServersMessage + "\nFleet Beacon Rush has no deployment."));
                Assert.That(asked.State, Is.EqualTo(InfrastructureState.NoGameServers));
                Assert.That(asked.AppPublicId, Is.EqualTo(ClientHarness.AppId));
                Assert.That(asked.Message, Is.EqualTo(InfrastructureCheck.NoGameServersMessage));
            }

            asked = null;
            InfrastructureReport none = await InfrastructureCheck.RunAsync(string.Empty, null, new TestScheduler(), CancellationToken.None);
            Assert.That(none.EditorDetail, Is.EqualTo("Fleet Beacon Rush has no deployment."), "no app id asks too");
            Assert.That(asked.AppPublicId, Is.Null);
        }

        [Test]
        public async Task TheEditorIsNeverAskedWhenItCannotExplainTheState()
        {
            int asks = 0;
            InfrastructureEditorHook.Register((q, ct) =>
            {
                asks++;
                return Task.FromResult("never shown");
            });
            using (var h = new ClientHarness(maxAttempts: 1))
            {
                h.Http.On("GET", ClientHarness.ServersPath, Step.Json(200, Fixture("server-list.json")), Step.Unreachable(), Step.Json(503, Fixture("error.degraded.json")));
                Assert.That((await Run(h)).EditorDetail, Is.Null, "ok");
                Assert.That((await Run(h)).EditorDetail, Is.Null, "unreachable");
                Assert.That((await Run(h)).EditorDetail, Is.Null, "a Discovery error");
            }

            Assert.That(asks, Is.EqualTo(0));
            Assert.That(InfrastructureCheck.AsksEditor(InfrastructureState.NoAppId) && InfrastructureCheck.AsksEditor(InfrastructureState.AppUnknown) && InfrastructureCheck.AsksEditor(InfrastructureState.NoGameServers), Is.True);
        }

        [Test]
        public async Task ASlowEditorAnswerIsDroppedAfterTheTimeoutAndItsReadsAreCancelled()
        {
            CancellationToken seen = default;
            var never = new TaskCompletionSource<string>();
            InfrastructureEditorHook.Register((q, ct) =>
            {
                seen = ct;
                return never.Task;
            });
            var scheduler = new TestScheduler();
            InfrastructureReport report = await scheduler.RunAsync(InfrastructureCheck.RunAsync(string.Empty, null, scheduler, CancellationToken.None));
            Assert.That(report.State, Is.EqualTo(InfrastructureState.NoAppId));
            Assert.That(report.EditorDetail, Is.Null, "the message shows without the detail");
            Assert.That(scheduler.Delays, Does.Contain(InfrastructureCheck.EditorDetailTimeout), "it waited on the game's scheduler, 5 s");
            Assert.That(seen.IsCancellationRequested, Is.True, "the answerer's reads are told to stop");
        }

        [Test]
        public async Task AnAnswererThatBlocksBeforeReturningHoldsNeitherTheCallerNorTheCheckPastTheTimeout()
        {
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            int answererThread = 0;
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                InfrastructureEditorHook.Register((q, ct) =>
                {
                    // Synchronous work before any task exists: a slow settings read, a credential store, a stuck call.
                    answererThread = Thread.CurrentThread.ManagedThreadId;
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return Task.FromResult("Fleet Beacon Rush has no deployment.");
                });
                try
                {
                    var scheduler = new TestScheduler();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Task<InfrastructureReport> running = InfrastructureCheck.RunAsync(string.Empty, null, scheduler, CancellationToken.None);
                    Assert.That(clock.ElapsedMilliseconds, Is.LessThan(2000), "the check returned to its caller while the answerer blocks [mutation: call the answerer on the caller's thread]");

                    InfrastructureReport report = await scheduler.RunAsync(running);
                    Assert.That(report.State, Is.EqualTo(InfrastructureState.NoAppId));
                    Assert.That(report.EditorDetail, Is.Null, "the timeout won; the late answer is dropped");
                    Assert.That(scheduler.Delays, Is.EqualTo(new[] { InfrastructureCheck.EditorDetailTimeout }), "the race started before the answerer and waited 5 s on the game's scheduler");
                    Assert.That(release.IsSet, Is.False, "the check finished while the answerer was still blocked");
                    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the answerer was asked");
                    Assert.That(answererThread, Is.Not.EqualTo(callerThread), "the answerer ran off the caller's thread");
                }
                finally
                {
                    release.Set();
                }
            }
        }

        [Test]
        public async Task AThrowingOrFaultingAnswererGivesNoDetail()
        {
            InfrastructureEditorHook.Register((q, ct) => throw new InvalidOperationException("boom"));
            Assert.That((await InfrastructureCheck.RunAsync(string.Empty, null, new TestScheduler(), CancellationToken.None)).EditorDetail, Is.Null);
            InfrastructureEditorHook.Register((q, ct) => Task.FromException<string>(new InvalidOperationException("boom")));
            Assert.That((await InfrastructureCheck.RunAsync(string.Empty, null, new TestScheduler(), CancellationToken.None)).EditorDetail, Is.Null);
            InfrastructureEditorHook.Register((q, ct) => null);
            Assert.That((await InfrastructureCheck.RunAsync(string.Empty, null, new TestScheduler(), CancellationToken.None)).EditorDetail, Is.Null);
        }

        [Test]
        public async Task AnEditorAnswerShapedLikeACredentialIsNeverShown()
        {
            string token = "usr" + "_" + "abcdefghijklmnop1234";
            InfrastructureEditorHook.Register((q, ct) => Task.FromResult("Signed in with " + token));
            InfrastructureReport report = await InfrastructureCheck.RunAsync(string.Empty, null, new TestScheduler(), CancellationToken.None);
            Assert.That(report.EditorDetail, Is.Null);
            Assert.That(report.ToString(), Does.Not.Contain(token));

            Assert.That(InfrastructureEditorHook.Clean("  " + new string('x', 500)), Has.Length.EqualTo(InfrastructureEditorHook.MaxDetailLength));
            Assert.That(InfrastructureEditorHook.Clean("dsc" + "_x"), Is.Null);
            Assert.That(InfrastructureEditorHook.Clean("Fleet app dscp_0123456789abcdef0123456789abcdef"), Is.Not.Null, "a public id is not a credential");
            Assert.That(InfrastructureEditorHook.Clean("   "), Is.Null);
        }

        [Test]
        public void TheEditorHookCompilesOnlyInTheEditor()
        {
            // Nothing of the hook may reach a player build: its file is wrapped whole, and the check calls it only under
            // the same define.
            string runtime = Path.Combine(PackageRoot, "Runtime", "Discovery", "Infrastructure");
            string[] hook = File.ReadAllLines(Path.Combine(runtime, "InfrastructureEditorHook.cs")).Where(l => l.Trim().Length > 0).ToArray();
            Assert.That(hook.First().Trim(), Is.EqualTo("#if UNITY_EDITOR"));
            Assert.That(hook.Last().Trim(), Is.EqualTo("#endif"));
            Assert.That(hook.Count(l => l.TrimStart().StartsWith("#", StringComparison.Ordinal)), Is.EqualTo(2), "no other directive inside");

            string check = File.ReadAllText(Path.Combine(runtime, "InfrastructureCheck.cs"));
            int ifEditor = check.IndexOf("#if UNITY_EDITOR", StringComparison.Ordinal);
            int elseBranch = check.IndexOf("#else", ifEditor, StringComparison.Ordinal);
            Assert.That(ifEditor, Is.GreaterThan(0));
            Assert.That(check.Substring(0, ifEditor), Does.Not.Contain("InfrastructureEditorHook."), "no hook call outside the Editor branch");
            Assert.That(check.Substring(elseBranch), Does.Not.Contain("InfrastructureEditorHook."), "the player branch never names the hook");
        }

        private static Task<InfrastructureReport> Run(ClientHarness h)
        {
            return h.Scheduler.RunAsync(InfrastructureCheck.RunAsync(ClientHarness.AppId, h.Client, h.Scheduler, CancellationToken.None));
        }

        private static string PackageRoot
        {
            get
            {
                PackageInfo info = PackageInfo.FindForAssembly(typeof(DiscoveryClient).Assembly);
                Assert.That(info?.resolvedPath, Is.Not.Null.And.Not.Empty, "io.pingcore.sdk is not resolved as a package");
                return Path.GetFullPath(info.resolvedPath);
            }
        }

        private static string DiscoveryContracts
        {
            get
            {
                string dir = Path.GetFullPath(Path.Combine(PackageRoot, "..", "..", "contracts", "discovery"));
                Assert.That(Directory.Exists(dir), Is.True, "contracts/discovery is beside the package in this repository and in the public mirror: " + dir);
                return dir;
            }
        }

        private static JObject Fixture(string name)
        {
            JObject fixture = JObject.Parse(File.ReadAllText(Path.Combine(DiscoveryContracts, "fixtures", name)));
            Assert.That(fixture.Value<string>("fixture"), Is.EqualTo("pingcore-fixture/1"), name);
            return (JObject)fixture["payload"].DeepClone();
        }
    }
}
