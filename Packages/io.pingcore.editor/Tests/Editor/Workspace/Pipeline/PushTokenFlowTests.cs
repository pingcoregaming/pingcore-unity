using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Tests.Fakes;
using static PingCore.Editor.Workspace.Tests.Pipeline.PipelineFixtures;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>
    /// The push token is issued once per CDN source: only after the confirmation, only when the store holds none
    /// (issuing replaces the token for every holder), and replaced only when the developer asks for exactly that.
    /// </summary>
    public sealed class PushTokenFlowTests
    {
        private const string Host = "studio.app.pingcore.io";

        private FakeCredentialStore store;
        private FakePingCoreApi api;
        private PushTokenFlow flow;

        [SetUp]
        public void SetUp()
        {
            store = new FakeCredentialStore();
            api = new FakePingCoreApi(Host) { IssuePushToken = id => (FakePushToken(), "2026-10-08 09:00:00", null) };
            flow = new PushTokenFlow(store, Host);
        }

        [Test]
        public async Task ATokenIsIssuedIntoTheStoreOnlyAfterTheConfirmation()
        {
            ApiResult<SecretReceipt> unconfirmed = await flow.IssueAsync(api, 31, false, false, CancellationToken.None);
            Assert.That(unconfirmed.Error.NotSent, Is.True);
            Assert.That(api.Calls, Is.Empty, "[mutation: issue without asking]");

            ApiResult<SecretReceipt> issued = await flow.IssueAsync(api, 31, true, false, CancellationToken.None);
            Assert.That(issued.Ok, Is.True, issued.Error?.ToString());
            Assert.That(api.Calls, Is.EqualTo(new[] { "POST cdn-sources/sources/31/push-token" }));
            Assert.That(store.Read(CredentialTargets.PushToken(Host, 31)).Secret, Is.EqualTo(FakePushToken()));
            Assert.That(flow.Presence(31, out _), Is.EqualTo(PushTokenPresence.Stored));
        }

        [Test]
        public async Task AStoredTokenIsNeverReplacedUnlessTheDeveloperAsksForExactlyThat()
        {
            store.Seed(CredentialTargets.PushToken(Host, 31), "cdnpush_" + new string('o', 30));
            ApiResult<SecretReceipt> refused = await flow.IssueAsync(api, 31, true, false, CancellationToken.None);
            Assert.That(refused.Ok, Is.False, "[mutation: issue on every push]");
            Assert.That(refused.Error.Message, Does.Contain("already stored"));
            Assert.That(api.Calls, Is.Empty, "another holder's copy keeps working");

            ApiResult<SecretReceipt> replaced = await flow.IssueAsync(api, 31, true, true, CancellationToken.None);
            Assert.That(replaced.Ok, Is.True);
            Assert.That(store.Read(CredentialTargets.PushToken(Host, 31)).Secret, Is.EqualTo(FakePushToken()));
        }

        [Test]
        public async Task AStoreThatCannotBeReadIssuesNothingRatherThanCountingAsNoToken()
        {
            store.FailNextWith("the vault is locked");
            Assert.That(flow.Presence(31, out string problem), Is.EqualTo(PushTokenPresence.Unreadable), "[mutation: an unreadable store reads as none]");
            Assert.That(problem, Does.Contain("could not be read").And.Contain("the vault is locked"));

            store.FailNextWith("the vault is locked");
            ApiResult<SecretReceipt> refused = await flow.IssueAsync(api, 31, true, false, CancellationToken.None);
            Assert.That(refused.Ok, Is.False);
            Assert.That(refused.Error.Message, Does.Contain("could not be read"));
            Assert.That(api.Calls, Is.Empty, "[mutation: issue when the store cannot say] every other holder's token keeps working");

            store.FailNextWith("the vault is locked");
            Assert.That((await flow.IssueAsync(api, 31, true, true, CancellationToken.None)).Ok, Is.False, "Replace refuses too: the new token could not be kept");
            Assert.That(api.Calls, Is.Empty);
        }

        [Test]
        public async Task NoSourceOrAnotherWorkspacesClientIssuesNothing()
        {
            Assert.That((await flow.IssueAsync(api, 0, true, false, CancellationToken.None)).Error.Message, Does.Contain("The branch names no CDN source"));
            var other = new FakePingCoreApi("other.app.pingcore.io") { IssuePushToken = id => (FakePushToken(), null, null) };
            Assert.That((await flow.IssueAsync(other, 31, true, false, CancellationToken.None)).Error.Message, Does.Contain("another workspace"));
            Assert.That(other.Calls, Is.Empty);
            Assert.That(store.Targets, Is.Empty);
        }

        [Test]
        public void TheConfirmationNamesTheCostForEveryOtherHolder()
        {
            string text = PushTokenFlow.IssueConfirmationText(31);
            Assert.That(text, Does.Contain("CDN source 31").And.Contain("replaces its current token at once"));
            Assert.That(text, Does.Contain("CI").And.Contain("teammate").And.Contain("never shown"));
            Assert.That(text, Does.Contain("If a teammate or CI already pushes to this source, use their token instead (Use an existing push token)."), "the issue path names the alternative");
        }

        // "Use an existing push token": a token issued elsewhere, checked with PingCore, kept under the issue path's target.

        private static ApiResult<PushInfoResponse> SourceOf(long sourceId)
            => ApiResult<PushInfoResponse>.Success(new PushInfoResponse { Source = new PushInfoSourceView { SourceId = sourceId, Name = "Beacon Rush Linux" } });

        // Everything a developer could see or a log could keep: the receipt, its message, the error, the calls.
        private static string Visible(ApiResult<SecretReceipt> result, FakePingCoreApi fake)
            => (result.Ok ? result.Value.Target + result.Value.StoreKind + result.Value.Created + result.Message : result.Error.ToString()) + string.Join(" ", fake.Calls);

        [Test]
        public async Task AnExistingTokenForTheBranchsSourceIsCheckedThenKeptUnderTheIssuePathsTarget()
        {
            string pasted = "cdnpush_" + "Pz4" + new string('m', 40) + "Q1";
            api.CheckPushToken = token => SourceOf(31);
            ApiResult<SecretReceipt> kept = await flow.UseExistingAsync(api, 31, "  " + pasted + " ", () => throw new AssertionException("nothing stored: no confirmation"), CancellationToken.None);

            Assert.That(kept.Ok, Is.True, kept.Error?.ToString());
            Assert.That(api.CheckedTokens, Is.EqualTo(new[] { pasted }), "checked with PingCore, trimmed");
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET cdn-sources/push/info" }), "[mutation: issue a token] nothing is issued, so no other holder is affected");
            Assert.That(kept.Value.Target, Is.EqualTo(CredentialTargets.PushToken(Host, 31)));
            Assert.That(store.Read(CredentialTargets.PushToken(Host, 31)).Secret, Is.EqualTo(pasted));
            Assert.That(Visible(kept, api), Does.Not.Contain(pasted).And.Not.Contain("Pz4mmm"), "the token is never in the receipt or the message");
        }

        [Test]
        public async Task ATokenOfAnotherSourceIsRefusedNamingBothSourcesAndNothingIsStored()
        {
            string pasted = FakePushToken();
            api.CheckPushToken = token => SourceOf(44);
            ApiResult<SecretReceipt> refused = await flow.UseExistingAsync(api, 31, pasted, () => true, CancellationToken.None);
            Assert.That(refused.Ok, Is.False, "[mutation: keep a token of any source]");
            Assert.That(refused.Error.Message, Is.EqualTo("This push token belongs to CDN source #44, not #31. Nothing was stored."));
            Assert.That(store.Targets, Is.Empty);
            Assert.That(Visible(refused, api), Does.Not.Contain(pasted));
        }

        [Test]
        public async Task ATokenPingCoreRejectsOrAnUnansweredCheckStoresNothing()
        {
            string pasted = FakePushToken();
            api.CheckPushToken = token => ApiResult<PushInfoResponse>.Failure(new PluginError("push-token", PluginErrorKind.Rejected, PushTokenShape.NotAcceptedMessage, PushTokenShape.NotAcceptedHint) { HttpStatus = 401 });
            ApiResult<SecretReceipt> rejected = await flow.UseExistingAsync(api, 31, pasted, () => true, CancellationToken.None);
            Assert.That(rejected.Error.Message, Does.StartWith("PingCore did not accept this push token."));
            Assert.That(store.Targets, Is.Empty, "[mutation: store before the check answers]");

            api.CheckPushToken = token => ApiResult<PushInfoResponse>.Failure(new PluginError("push-token", PluginErrorKind.Transport, "GET cdn-sources/push/info: no answer from the workspace (timeout).", null));
            ApiResult<SecretReceipt> lost = await flow.UseExistingAsync(api, 31, pasted, () => true, CancellationToken.None);
            Assert.That(lost.Error.Kind, Is.EqualTo(PluginErrorKind.Transport));
            Assert.That(store.Targets, Is.Empty, "a transport error leaves nothing stored");
            Assert.That(Visible(rejected, api) + Visible(lost, api), Does.Not.Contain(pasted));
        }

        [TestCase("", TestName = "Use an existing push token: an empty paste is refused before any call")]
        [TestCase("usr_" + "0123456789abcdefABCD", TestName = "Use an existing push token: a usr_ key is refused before any call")]
        [TestCase("cdnpush_abc", TestName = "Use an existing push token: a short value is refused before any call")]
        [TestCase("dsc_" + "0123456789abcdef0123", TestName = "Use an existing push token: a Discovery token is refused before any call")]
        public async Task AValueThatIsNotPushTokenShapedIsRefusedWithoutBeingEchoed(string pasted)
        {
            ApiResult<SecretReceipt> refused = await flow.UseExistingAsync(api, 31, pasted, () => true, CancellationToken.None);
            Assert.That(refused.Error.Message, Is.EqualTo(PushTokenShape.NotATokenMessage));
            Assert.That(api.Calls, Is.Empty);
            Assert.That(store.Targets, Is.Empty);
            if (pasted.Length > 0)
            {
                Assert.That(refused.Error.ToString(), Does.Not.Contain(pasted));
            }
        }

        [Test]
        public async Task AStoredTokenIsReplacedOnlyAfterTheConfirmation()
        {
            string stored = "cdnpush_" + new string('o', 30);
            string pasted = FakePushToken();
            store.Seed(CredentialTargets.PushToken(Host, 31), stored);
            api.CheckPushToken = token => SourceOf(31);
            int asked = 0;

            ApiResult<SecretReceipt> declined = await flow.UseExistingAsync(api, 31, pasted, () => { asked++; return false; }, CancellationToken.None);
            Assert.That(declined.Ok, Is.False);
            Assert.That(asked, Is.EqualTo(1));
            Assert.That(store.Read(CredentialTargets.PushToken(Host, 31)).Secret, Is.EqualTo(stored), "[mutation: replace without asking]");

            ApiResult<SecretReceipt> replaced = await flow.UseExistingAsync(api, 31, pasted, () => { asked++; return true; }, CancellationToken.None);
            Assert.That(replaced.Ok, Is.True);
            Assert.That(asked, Is.EqualTo(2));
            Assert.That(store.Read(CredentialTargets.PushToken(Host, 31)).Secret, Is.EqualTo(pasted));
            Assert.That(PushTokenFlow.ReplaceStoredConfirmationText(31), Does.Contain("CDN source 31").And.Contain("not revoked"));
        }

        [Test]
        public async Task ATokenStoredWhileTheCheckRanIsNeverReplacedWithoutTheConfirmation()
        {
            string other = "cdnpush_" + new string('n', 30);
            api.CheckPushToken = token =>
            {
                store.Seed(CredentialTargets.PushToken(Host, 31), other);
                return SourceOf(31);
            };
            ApiResult<SecretReceipt> declined = await flow.UseExistingAsync(api, 31, FakePushToken(), () => false, CancellationToken.None);
            Assert.That(declined.Ok, Is.False, "[mutation: decide on the presence read before the network call]");
            Assert.That(store.Read(CredentialTargets.PushToken(Host, 31)).Secret, Is.EqualTo(other));
        }

        [Test]
        public async Task NoSourceAnotherWorkspaceOrAnUnreadableStoreKeepsNothingAndChecksNothing()
        {
            api.CheckPushToken = token => SourceOf(31);
            Assert.That((await flow.UseExistingAsync(api, 0, FakePushToken(), () => true, CancellationToken.None)).Error.Message, Does.Contain("names no CDN source"));
            var other = new FakePingCoreApi("other.app.pingcore.io") { CheckPushToken = token => SourceOf(31) };
            Assert.That((await flow.UseExistingAsync(other, 31, FakePushToken(), () => true, CancellationToken.None)).Error.Message, Does.Contain("another workspace"));
            store.FailNextWith("the vault is locked");
            Assert.That((await flow.UseExistingAsync(api, 31, FakePushToken(), () => true, CancellationToken.None)).Error.Message, Does.Contain("could not be read").And.Contain("Nothing was stored"));
            Assert.That(api.Calls.Concat(other.Calls), Is.Empty);
            Assert.That(store.Targets, Is.Empty);
        }

        [Test]
        public async Task TheIssuePathIsUnchangedByTheExistingTokenPath()
        {
            ApiResult<SecretReceipt> issued = await flow.IssueAsync(api, 31, true, false, CancellationToken.None);
            Assert.That(issued.Ok, Is.True);
            Assert.That(api.Calls, Is.EqualTo(new[] { "POST cdn-sources/sources/31/push-token" }));
            Assert.That(api.CheckedTokens, Is.Empty, "issuing checks nothing with the push info route");
        }
    }
}
