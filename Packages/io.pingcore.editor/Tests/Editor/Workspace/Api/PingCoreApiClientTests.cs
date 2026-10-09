using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Redaction;
using PingCore.Editor.Workspace.Tests.Fakes;
using PingCore.Editor.Workspace.Tests.Pipeline;

namespace PingCore.Editor.Workspace.Tests.Api
{
    /// <summary>
    /// The client over a scripted transport and an in-memory key store: URL and bearer, the key
    /// read per call and never sent without one, typed values from the fixtures, transport
    /// failures without URLs, and the shown-once push token going straight into the store.
    /// </summary>
    public sealed class PingCoreApiClientTests
    {
        private const string Host = "studio.app.pingcore.io";
        private static readonly string Key = "usr_" + "0123456789abcdefABCD";

        private FakeHttpTransport transport;
        private FakeCredentialStore keys;
        private PingCoreApiClient client;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeHttpTransport();
            keys = new FakeCredentialStore();
            keys.Seed(CredentialTargets.UserKey(Host), Key);
            client = new PingCoreApiClient(WorkspaceEndpoint.ForHost(Host), keys, transport);
        }

        [Test]
        public async Task ACallSendsTheBearerToTheHttpsRouteAndAnswersTheTypedData()
        {
            transport.Answer(200, WorkspaceFixtures.Envelope("fleets.live.json", "Fleet live state."));
            ApiResult<FleetLiveResponse> result = await client.GetFleetLiveAsync(1, CancellationToken.None);

            Assert.That(result.Ok, Is.True, result.Error?.ToString());
            Assert.That(result.Value.Deployments.Single().Servers.Single().ServerId, Is.EqualTo("4211"));
            Assert.That(result.Message, Is.EqualTo("Fleet live state."));
            PingCoreHttpRequest sent = transport.Requests.Single();
            Assert.That(sent.Method, Is.EqualTo("GET"));
            Assert.That(sent.Url, Is.EqualTo("https://studio.app.pingcore.io/api/fleets/1/live"));
            Assert.That(sent.Headers["Authorization"], Is.EqualTo("Bearer " + Key));
            Assert.That(sent.Body, Is.Null);
        }

        [Test]
        public async Task WithNoStoredKeyNothingIsSentAndTheAnswerIsNotSignedIn()
        {
            keys.Delete(CredentialTargets.UserKey(Host));
            ApiResult<FleetListResponse> result = await client.ListFleetsAsync(CancellationToken.None);
            Assert.That(result.Error.Kind, Is.EqualTo(PluginErrorKind.NotSignedIn));
            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public async Task TheKeyIsReadFromTheStoreOnEveryCall()
        {
            transport.Handler = _ => new PingCoreHttpResponse(200, null, WorkspaceFixtures.Envelope("fleets.list.json"));
            await client.ListFleetsAsync(CancellationToken.None);
            string other = "usr_" + "ZYXWVUTSRQPONMLKJIHG";
            keys.Seed(CredentialTargets.UserKey(Host), other);
            await client.ListFleetsAsync(CancellationToken.None);
            Assert.That(transport.Requests.Select(r => r.Headers["Authorization"]), Is.EqualTo(new[] { "Bearer " + Key, "Bearer " + other }));
            Assert.That(keys.Operations.Count(o => o.StartsWith("read ")), Is.EqualTo(2));
        }

        [Test]
        public async Task ATransportFailureNamesTheRouteTemplateNeverTheUrlOrTheKey()
        {
            transport.ThrowNext = new InvalidOperationException("boom https://studio.app.pingcore.io/api/fleets/1/releases/12 " + Key);
            ApiResult<ReleaseDetailResponse> result = await client.GetReleaseAsync(1, 12, CancellationToken.None);
            Assert.That(result.Error.Kind, Is.EqualTo(PluginErrorKind.Transport));
            Assert.That(result.Error.Message, Does.Contain("fleets/{fleetId}/releases/{releaseId}"));
            Assert.That(result.Error.Message, Does.Not.Contain("/12").And.Not.Contain(Key).And.Not.Contain("boom"));

            transport.ThrowNext = new PingCoreTransportException("No answer within 30 s.");
            Assert.That((await client.ListFleetsAsync(CancellationToken.None)).Error.Message, Does.Contain("No answer within 30 s."));
        }

        [Test]
        public async Task AReleaseSendsItsTargetAndTheStartupCommandReadsAreGets()
        {
            transport.Answer(200, WorkspaceFixtures.Envelope("releases.create.json"));
            ApiResult<ReleaseCreatedResponse> created = await client.CreateReleaseAsync(1, new ReleaseCreateRequest { TargetBuildVersion = "2026.10.06-editor1" }, CancellationToken.None);
            Assert.That(created.Value.Release.ReleaseId, Is.EqualTo(17));
            Assert.That(transport.Requests[0].Body, Is.EqualTo("{\"targetBuildVersion\":\"2026.10.06-editor1\"}"));

            ApiResult<ReleaseCreatedResponse> refused = await client.CreateReleaseAsync(1, new ReleaseCreateRequest(), CancellationToken.None);
            Assert.That(refused.Error.Kind, Is.EqualTo(PluginErrorKind.Refused));
            Assert.That(transport.Requests.Count, Is.EqualTo(1), "a release with no target is never sent");

            transport.Answer(200, WorkspaceFixtures.Envelope("game.get.json"));
            transport.Answer(200, WorkspaceFixtures.Envelope("deployment-specs.list.json"));
            transport.Answer(200, WorkspaceFixtures.Envelope("template-set.get.json"));
            Assert.That((await client.GetGameBranchesAsync(9001, CancellationToken.None)).Ok, Is.True);
            Assert.That((await client.ListDeploymentSpecsAsync(9001, CancellationToken.None)).Ok, Is.True);
            Assert.That((await client.GetTemplateSetAsync(9001, 2301, CancellationToken.None)).Ok, Is.True);
            Assert.That(transport.Requests.Skip(1).Select(r => r.Method + " " + r.Url), Is.EqualTo(new[]
            {
                "GET https://studio.app.pingcore.io/api/my-games/9001",
                "GET https://studio.app.pingcore.io/api/my-games/9001/kubernetes/deployment-specs",
                "GET https://studio.app.pingcore.io/api/my-games/9001/template-sets/2301",
            }));
            Assert.That(transport.Requests.Skip(1).Select(r => r.Body), Is.All.Null, "reads send no body");
        }

        [Test]
        public async Task AnIssuedPushTokenGoesStraightIntoTheStoreAndNeverIntoTheResult()
        {
            string token = "cdnpush_" + new string('q', 40);
            transport.Answer(200, "{\"error\":false,\"message\":\"Push token issued.\",\"data\":{\"token\":\"" + token + "\",\"created\":\"2026-10-06 12:00:00\"}}");
            var secrets = new FakeCredentialStore();
            ApiResult<SecretReceipt> receipt = await client.IssuePushTokenAsync(31, secrets, CancellationToken.None);

            Assert.That(receipt.Ok, Is.True, receipt.Error?.ToString());
            Assert.That(receipt.Value.Target, Is.EqualTo("PingCore/studio.app.pingcore.io/cdnpush/31"));
            Assert.That(secrets.Read(receipt.Value.Target).Secret, Is.EqualTo(token));
            Assert.That(receipt.Value.Created, Is.EqualTo("2026-10-06 12:00:00"));
            Assert.That(typeof(SecretReceipt).GetProperties().Select(p => p.Name), Has.None.EqualTo("Token").And.None.EqualTo("Secret"));
            Assert.That(transport.Requests.Single().Url, Is.EqualTo("https://studio.app.pingcore.io/api/cdn-sources/sources/31/push-token"));
        }

        [Test]
        public async Task TheGameReadKeepsOnlyTheModelledKeysAndDropsTheRestUnread()
        {
            // Keys the plugin does not model, planted on the game, a branch and a template set (a value, an object and a
            // list), around the pinned ones: none of it may reach the result.
            string unmodelled = "unmodelled-" + "value-" + "kept-out";
            JObject answer = (JObject)WorkspaceFixtures.Load("game.get.json")["payload"].DeepClone();
            answer["brandId"] = 5;
            answer["unmodelledGameKey"] = unmodelled;
            var branch = (JObject)answer["gameBranches"][0];
            branch["unmodelledBranchKey"] = unmodelled;
            branch["unmodelledBranchObject"] = new JObject { ["inner"] = new JObject { ["value"] = unmodelled } };
            branch["unmodelledBranchList"] = new JArray(new JObject { ["value"] = unmodelled });
            branch["deploymentSpecs"] = new JArray(new JObject { ["specId"] = 5801, ["specName"] = "beacon-rush-linux" });
            branch["defaultDeploymentSpecId"] = 5801;
            answer["templateSets"][0]["created"] = "2026-09-01 12:00:00";
            answer["templateSets"][0]["description"] = "the startup command";
            transport.Answer(200, Envelope(answer, "Game details retrieved successfully"));

            ApiResult<GameBranchesResponse> game = await client.GetGameBranchesAsync(9001, CancellationToken.None);

            Assert.That(game.Ok, Is.True, game.Error?.ToString());
            Assert.That((game.Value.GameId, game.Value.Name), Is.EqualTo((9001L, "Beacon Rush")));
            GameBranchView read = game.Value.GameBranches.Single();
            Assert.That((read.GameBranchId, read.DataSourceType, read.CdnSourceId, read.Platform), Is.EqualTo((4201L, "cdn_source", (long?)31, "linux")));
            Assert.That(game.Value.TemplateSets.Select(s => (s.TemplateSetId, s.SetName)), Is.EqualTo(new[] { (2301L, "Default") }), "the game's template sets, id and name");
            Assert.That(typeof(GameBranchesResponse).GetProperties().Select(p => p.Name), Is.EqualTo(new[] { "GameId", "Name", "GameBranches", "TemplateSets" }), "nothing else of the game is modelled");
            Assert.That(typeof(GameBranchView).GetProperties().Select(p => p.Name),
                Is.EqualTo(new[] { "GameBranchId", "BranchName", "BranchDescription", "Platform", "DefaultBranch", "DataSourceType", "CdnSourceId" }), "nothing else of a branch is modelled, not its legacy default deployment spec");
            Assert.That(typeof(GameTemplateSetView).GetProperties().Select(p => p.Name), Is.EqualTo(new[] { "TemplateSetId", "SetName" }));
            string everything = JsonConvert.SerializeObject(game) + game.Message;
            Assert.That(everything, Does.Not.Contain(unmodelled), "[mutation: model the whole answer] nothing unmodelled reaches the result");
            Assert.That(everything, Does.Not.Contain("unmodelledBranch"), "not even the key's name is kept");
            Assert.That(everything, Does.Not.Contain("defaultDeploymentSpecId").And.Not.Contain("the startup command"), "the legacy default spec and the sets' other columns are dropped");
        }

        [Test]
        public async Task TheDeploymentReadKeepsOnlyItsIdNameAndSpec()
        {
            // Keys the plugin does not model, planted around the pinned ones: none of it may reach the result.
            string address = "203.0.113." + "77";
            JObject answer = (JObject)WorkspaceFixtures.Load("deployments.get.json")["payload"].DeepClone();
            var deployment = (JObject)answer["deployment"];
            deployment["brandId"] = 5;
            deployment["unmodelledText"] = "{\"2394011\":\"1234567890123456789\"}";
            deployment["unmodelledRows"] = new JArray(new JObject { ["rowId"] = 4211, ["address"] = address, ["port"] = 7777 });
            deployment["fleet"] = new JObject { ["fleetId"] = 42, ["name"] = "Beacon Rush" };
            transport.Answer(200, Envelope(answer, ""));

            ApiResult<DeploymentReadResponse> read = await client.GetDeploymentAsync(100, CancellationToken.None);

            Assert.That(read.Ok, Is.True, read.Error?.ToString());
            Assert.That((read.Value.Deployment.BrandDeploymentId, read.Value.Deployment.DeploymentSpecId, read.Value.Deployment.FriendlyName), Is.EqualTo((100L, 5801L, "eu-1")));
            Assert.That(transport.Requests.Single().Url, Is.EqualTo("https://studio.app.pingcore.io/api/brand/servers/deployments/100"));
            Assert.That(typeof(DeploymentReadResponse).GetProperties().Select(p => p.Name), Is.EqualTo(new[] { "Deployment" }));
            Assert.That(typeof(DeploymentReadView).GetProperties().Select(p => p.Name), Is.EqualTo(new[] { "BrandDeploymentId", "DeploymentSpecId", "FriendlyName" }), "nothing else of the deployment is modelled");
            string everything = JsonConvert.SerializeObject(read) + read.Message;
            Assert.That(everything, Does.Not.Contain(address).And.Not.Contain("unmodelledRows").And.Not.Contain("1234567890123456789"), "[mutation: model the whole answer] nothing unmodelled reaches the result");
        }

        [Test]
        public async Task APastedPushTokenIsSentOnlyAsTheBearerAndOnlyTheSourceOfTheAnswerIsKept()
        {
            string pasted = PipelineFixtures.FakePushToken();
            string unmodelled = "unmodelled-" + "value-" + "kept-out";
            JObject answer = (JObject)WorkspaceFixtures.Load("push-info.get.json")["payload"].DeepClone();
            ((JObject)answer["source"])["unmodelledSourceKey"] = "5/beacon/linux";
            answer["unmodelledObject"] = new JObject { ["a"] = "x", ["b"] = "y", ["c"] = unmodelled };
            answer["unmodelledValue"] = "snapshot_20990101_010000";
            transport.Answer(200, Envelope(answer, "Push info retrieved successfully"));
            int keyReads = keys.Operations.Count(o => o.StartsWith("read "));

            ApiResult<PushInfoResponse> info = await client.CheckPushTokenAsync("  " + pasted + "\n", CancellationToken.None);

            Assert.That(info.Ok, Is.True, info.Error?.ToString());
            Assert.That((info.Value.Source.SourceId, info.Value.Source.Name), Is.EqualTo((31L, "Beacon Rush Linux")));
            PingCoreHttpRequest sent = transport.Requests.Single();
            Assert.That(sent.Method + " " + sent.Url, Is.EqualTo("GET https://studio.app.pingcore.io/api/cdn-sources/push/info"));
            Assert.That(sent.Headers["Authorization"], Is.EqualTo("Bearer " + pasted), "the pasted token, trimmed, is the bearer [mutation: send the usr_ key]");
            Assert.That(sent.Url, Does.Not.Contain(pasted));
            Assert.That(sent.Body, Is.Null);
            Assert.That(typeof(PushInfoResponse).GetProperties().Select(p => p.Name), Is.EqualTo(new[] { "Source" }));
            Assert.That(typeof(PushInfoSourceView).GetProperties().Select(p => p.Name), Is.EqualTo(new[] { "SourceId", "Name" }));
            string everything = JsonConvert.SerializeObject(info) + info.Message;
            Assert.That(everything, Does.Not.Contain(unmodelled).And.Not.Contain("unmodelledSourceKey").And.Not.Contain(pasted), "nothing unmodelled, and not the token, reaches the result");
            Assert.That(keys.Operations.Count(o => o.StartsWith("read ")), Is.EqualTo(keyReads), "the brand member's key is not even read");
        }

        [Test]
        public async Task APushTokenPingCoreRefusesIsSaidPlainlyAndNeverQuoted()
        {
            string pasted = PipelineFixtures.FakePushToken();
            foreach (int status in new[] { 401, 403 })
            {
                transport.Answer(status, "{\"error\":true,\"message\":\"Invalid or revoked push token. Issue a new one on the source page in the workspace UI.\",\"data\":[]}");
                ApiResult<PushInfoResponse> refused = await client.CheckPushTokenAsync(pasted, CancellationToken.None);
                Assert.That(refused.Error.Kind, Is.EqualTo(PluginErrorKind.Rejected), status.ToString());
                Assert.That(refused.Error.Message, Does.StartWith("PingCore did not accept this push token."));
                Assert.That(refused.Error.Hint, Does.Not.Contain("usr_"), "not the sign-in hint: the bearer was a push token");
                Assert.That(refused.Error.ToString(), Does.Not.Contain(pasted));
            }

            transport.Answer(200, "{\"error\":false,\"message\":\"ok\",\"data\":{\"unmodelled\":{}}}");
            Assert.That((await client.CheckPushTokenAsync(pasted, CancellationToken.None)).Error.Kind, Is.EqualTo(PluginErrorKind.Envelope), "an answer that names no source");

            transport.ThrowNext = new InvalidOperationException("boom " + pasted);
            ApiResult<PushInfoResponse> lost = await client.CheckPushTokenAsync(pasted, CancellationToken.None);
            Assert.That(lost.Error.Kind, Is.EqualTo(PluginErrorKind.Transport));
            Assert.That(lost.Error.ToString(), Does.Not.Contain(pasted).And.Not.Contain("boom"));
        }

        [Test]
        public async Task APushTokenTheWorkspaceOrTheTransportQuotesBackIsMaskedInEveryMessage()
        {
            // The shortest token the shape check lets through; whichever redactor rule masks it, it must never be shown.
            string pasted = "cdnpush_" + "Qq7" + new string('w', 13);
            Assert.That(PushTokenShape.Matches(pasted), Is.True, "precondition: it is sent");
            var answers = new (int Status, string Body)[]
            {
                (401, "{\"error\":true,\"message\":\"Invalid push token " + pasted + "\",\"data\":[]}"),
                (400, "{\"error\":true,\"message\":\"Source for " + pasted + " uses steamcmd\",\"data\":[]}"),
                (502, "{\"error\":true,\"message\":\"CDN unavailable for " + pasted + "\",\"data\":[]}"),
            };
            foreach ((int status, string body) in answers)
            {
                transport.Answer(status, body);
                ApiResult<PushInfoResponse> refused = await client.CheckPushTokenAsync(pasted, CancellationToken.None);
                Assert.That(refused.Ok, Is.False, status.ToString());
                Assert.That(refused.Error.ToString() + global::PingCore.Editor.Workspace.UI.Common.ErrorText.Of(refused.Error), Does.Not.Contain(pasted), "[mutation: carry the server message unredacted] HTTP " + status);
            }

            transport.ThrowNext = new PingCoreTransportException("No answer from https://studio.app.pingcore.io for " + pasted);
            ApiResult<PushInfoResponse> lost = await client.CheckPushTokenAsync(pasted, CancellationToken.None);
            Assert.That(lost.Error.Kind, Is.EqualTo(PluginErrorKind.Transport));
            Assert.That(lost.Error.ToString() + global::PingCore.Editor.Workspace.UI.Common.ErrorText.Of(lost.Error), Does.Not.Contain(pasted), "a transport message that quotes the token");
        }

        [TestCase("", TestName = "A pasted push token: an empty paste is never sent")]
        [TestCase("usr_" + "0123456789abcdefABCD", TestName = "A pasted push token: a brand member key is never sent")]
        [TestCase("cdnpush_short", TestName = "A pasted push token: one too short is never sent")]
        [TestCase("cdnpush_" + "0123456789abcdef 0123", TestName = "A pasted push token: one with a space inside is never sent")]
        [TestCase("Bearer cdnpush_" + "0123456789abcdef0123", TestName = "A pasted push token: one with the header word is never sent")]
        public async Task SomethingThatIsNotAPushTokenIsNeverSent(string pasted)
        {
            ApiResult<PushInfoResponse> refused = await client.CheckPushTokenAsync(pasted, CancellationToken.None);
            Assert.That(refused.Error.Kind, Is.EqualTo(PluginErrorKind.Refused));
            Assert.That(refused.Error.NotSent, Is.True);
            Assert.That(refused.Error.Message, Is.EqualTo(PushTokenShape.NotATokenMessage));
            Assert.That(transport.Requests, Is.Empty);
        }

        private static string Envelope(JObject data, string message) => new JObject { ["error"] = false, ["message"] = message, ["data"] = data }.ToString(Formatting.None);

        [Test]
        public async Task AnAnswerWithoutACdnpushTokenStoresNothing()
        {
            transport.Answer(200, WorkspaceFixtures.Envelope("push-token.issue.json"));
            var secrets = new FakeCredentialStore();
            ApiResult<SecretReceipt> receipt = await client.IssuePushTokenAsync(31, secrets, CancellationToken.None);
            Assert.That(receipt.Error.Kind, Is.EqualTo(PluginErrorKind.Envelope), "the fixture's placeholder <token> is not a cdnpush_ token");
            Assert.That(secrets.Targets, Is.Empty);
        }

        [Test]
        public async Task AStoreFailureAfterIssuingSaysTheTokenMustBeIssuedAgain()
        {
            transport.Answer(200, "{\"error\":false,\"message\":\"ok\",\"data\":{\"token\":\"cdnpush_" + new string('q', 40) + "\",\"created\":null}}");
            var secrets = new FakeCredentialStore();
            secrets.FailNextWith("CredWriteW failed (Windows error 5).");
            ApiResult<SecretReceipt> receipt = await client.IssuePushTokenAsync(31, secrets, CancellationToken.None);
            Assert.That(receipt.Error.Kind, Is.EqualTo(PluginErrorKind.Refused));
            Assert.That(receipt.Error.Message, Does.Contain("issue a new one").And.Contain("Windows error 5").And.Not.Contain("cdnpush_q"));
        }

        [Test]
        public async Task TheHttp200ErrorTrueTrapIsAFailureThroughTheClient()
        {
            transport.Answer(200, "{\"error\":true,\"message\":\"Category not found\",\"data\":[]}");
            ApiResult<TemplateSetResponse> result = await client.GetTemplateSetAsync(9001, 2301, CancellationToken.None);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error.Kind, Is.EqualTo(PluginErrorKind.Rejected));
            Assert.That(result.Error.Step, Is.EqualTo("startup-command"));
        }

        [Test]
        public async Task ACancelledCallAnswersCancelled()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.That((await client.ListFleetsAsync(cts.Token)).Error.Kind, Is.EqualTo(PluginErrorKind.Cancelled));
            }

            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public void TheEditorTransportNeverFollowsARedirectAndRefusesPlainHttp()
        {
            using (System.Net.Http.HttpClientHandler handler = EditorHttpTransport.CreateHandler())
            {
                Assert.That(handler.AllowAutoRedirect, Is.False);
                Assert.That(handler.UseCookies, Is.False);
            }

            using (var real = new EditorHttpTransport(TimeSpan.FromSeconds(1)))
            {
                var request = new PingCoreHttpRequest("GET", "http://studio.app.pingcore.io/api/fleets", null, null);
                Assert.ThrowsAsync<PingCoreTransportException>(() => real.SendAsync(request, CancellationToken.None));
            }
        }

        [Test]
        public void TheEndpointAcceptsHttpsHostsOnlyAndBuildsTheApiBase()
        {
            Assert.That(WorkspaceEndpoint.TryParse("https://Studio.App.PingCore.io/", out WorkspaceEndpoint e, out _), Is.True);
            Assert.That(e.Host, Is.EqualTo("studio.app.pingcore.io"));
            Assert.That(e.ApiBase, Is.EqualTo("https://studio.app.pingcore.io/api/"));
            Assert.That(WorkspaceEndpoint.TryParse("studio.app.pingcore.io", out e, out _), Is.True);
            Assert.That(WorkspaceEndpoint.TryParse("https://studio.app.pingcore.io/api", out e, out _), Is.True);
            foreach (string bad in new[] { "http://studio.app.pingcore.io", "https://user:pw@studio.app.pingcore.io", "https://studio.app.pingcore.io/x", "https://studio.app.pingcore.io/?a=1", "ftp://x.io", "", "https://bad_host.io" })
            {
                Assert.That(WorkspaceEndpoint.TryParse(bad, out _, out string problem), Is.False, bad);
                Assert.That(problem, Is.Not.Empty);
            }

            Assert.Throws<ArgumentException>(() => WorkspaceEndpoint.ForHost("studio.app.pingcore.io").UrlFor("fleets?x=1"));
        }

        [Test]
        public void ThePluginNeverSendsAPingctlRouteAndChecksAPastedTokenOnPushInfoAlone()
        {
            Assert.That(WorkspaceRoutes.All.Where(r => r.Caller == WorkspaceRouteCaller.Pingctl).Select(r => r.Template),
                Is.EquivalentTo(new[] { "cdn-sources/push/info", "cdn-sources/push/publish", "cdn-sources/push/status" }));
            Assert.That(WorkspaceRoutes.All.Where(r => r.Caller == WorkspaceRouteCaller.PluginWithPushToken).Select(r => r.Method + " " + r.Template),
                Is.EqualTo(new[] { "GET cdn-sources/push/info" }), "a pasted push token goes to the read pingctl starts with, and nowhere else");
            Assert.That(Redactor.ContainsSecret(Key), Is.True, "precondition: the test key is token-shaped");
        }
    }
}
