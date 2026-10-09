using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Confirmation;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Tests.Fakes;

namespace PingCore.Editor.Workspace.Tests.Confirmation
{
    /// <summary>
    /// The confirmation writer's decision table, end to end against a fake workspace writing into a
    /// temporary project root. Every refusal row changes exactly one thing from a baseline that
    /// confirms (the first test), so each row can fail; every refusal also proves no file was
    /// written. Token-shaped values are assembled from fragments so this file never holds one.
    /// </summary>
    public sealed class ConfirmationWriterTests
    {
        private const string Tail16 = "0123456789abcdef";

        // SHA-256 of the UTF-8 bytes of Token, computed outside Unity (sha256sum); the guard's own tests pin the same value.
        private const string TokenDigest = "0c025ab414b861a4e9f281bd4952d9fcb8f8ffb2d20934712497200d77a57cc0";

        private const long OpenAppId = 1002;
        private const long PrivateAppId = 1001;

        private static readonly string Token = "dsc" + "_" + "open" + Tail16;
        private static readonly string Masked = "dsc" + "_...cdef";
        private static readonly string OtherMasked = "dsc" + "_...9999";
        private static readonly string OpenPublicId = "dscp" + "_" + "00112233445566778899aabbccddeeff";
        private static readonly string PrivatePublicId = "dscp" + "_" + "ffeeddccbbaa99887766554433221100";
        private static readonly DateTime Now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        private string projectRoot;

        [SetUp]
        public void SetUp()
        {
            projectRoot = Path.Combine(Path.GetTempPath(), "pingcore-confirm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, true);
            }
        }

        private static DiscoveryTokenView Row(string masked, string scope, bool active = true, long id = 19)
            => new DiscoveryTokenView { TokenId = id, Name = "t" + id, MaskedToken = masked, Scope = scope, IsActive = active };

        private static DiscoveryAppListItem Item(long id, string publicId, string mode, bool enabled = true)
            => new DiscoveryAppListItem { DiscoveryAppId = id, Name = "app" + id, PublicId = publicId, RegistrationMode = mode, Enabled = enabled };

        /// <summary>A workspace with the open app 1002 (one active heartbeat token ending in the token's four) and the private app 1001.</summary>
        private static FakePingCoreApi Workspace(Action<DiscoveryAppDetailResponse> changeOpenApp = null, bool listOpenApp = true)
        {
            var api = new FakePingCoreApi();
            var apps = new List<DiscoveryAppListItem> { Item(PrivateAppId, PrivatePublicId, "private") };
            if (listOpenApp)
            {
                apps.Add(Item(OpenAppId, OpenPublicId, "open"));
            }

            api.ListDiscoveryApps = () => ApiResult<DiscoveryAppListResponse>.Success(new DiscoveryAppListResponse { Apps = apps });
            api.GetDiscoveryApp = id =>
            {
                DiscoveryAppListItem item = apps.Single(a => a.DiscoveryAppId == id);
                var detail = new DiscoveryAppDetailResponse
                {
                    App = Item(item.DiscoveryAppId, item.PublicId, item.RegistrationMode, item.Enabled),
                    Tokens = id == OpenAppId
                        ? new List<DiscoveryTokenView> { Row(Masked, "heartbeat"), Row(OtherMasked, "allocate", id: 20) }
                        : new List<DiscoveryTokenView> { Row(Masked, "both") },
                };
                if (id == OpenAppId)
                {
                    changeOpenApp?.Invoke(detail);
                }

                return ApiResult<DiscoveryAppDetailResponse>.Success(detail);
            };
            return api;
        }

        private Task<ConfirmationResult> Confirm(FakePingCoreApi api, string token = null, string publicId = null, IEnumerable<string> stored = null)
        {
            return new HeartbeatTokenConfirmationWriter(api, () => Now)
                .ConfirmAsync(projectRoot, token ?? Token, publicId ?? OpenPublicId, stored ?? Array.Empty<string>(), CancellationToken.None);
        }

        private string RecordPath => BuildGuardConfirmationFile.GetPath(projectRoot);

        private void AssertRefused(ConfirmationResult result, ConfirmationOutcome outcome)
        {
            Assert.That(result.Outcome, Is.EqualTo(outcome), result.ToString());
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Written, Is.Null);
            Assert.That(File.Exists(RecordPath), Is.False, "a refusal must not write the confirmation file");
            Assert.That(result.ToString(), Does.Not.Contain("open" + Tail16), "a refusal never quotes the token");
            Assert.That(result.ToString(), Does.Not.Contain("cdef"), "a refusal never quotes the token's last four characters");
        }

        [Test]
        public async Task TheBaselineOpenAppWithOneMatchingHeartbeatTokenIsConfirmedAndWritten()
        {
            FakePingCoreApi api = Workspace();

            ConfirmationResult result = await Confirm(api);

            Assert.That(result.Outcome, Is.EqualTo(ConfirmationOutcome.Confirmed), result.ToString());
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET discovery/apps", "GET discovery/apps/1002" }));
            Assert.That(result.Written.tokenDigest, Is.EqualTo(TokenDigest));
            Assert.That(result.Written.appPublicId, Is.EqualTo(OpenPublicId));
            Assert.That(result.Written.scope, Is.EqualTo("heartbeat"));
            Assert.That(result.Written.registrationMode, Is.EqualTo("open"));
            Assert.That(result.Written.confirmedAt, Is.EqualTo("2026-10-06T12:00:00Z"));
            string text = File.ReadAllText(RecordPath);
            Assert.That(text, Does.Contain(TokenDigest));
            Assert.That(text, Does.Not.Contain("open" + Tail16), "the file holds the digest, never the token");
        }

        [Test]
        public async Task APrivateAppIsRefusedEvenWithAMatchingHeartbeatToken()
        {
            AssertRefused(await Confirm(Workspace(d => d.App.RegistrationMode = "private")), ConfirmationOutcome.AppNotOpen);
        }

        [Test]
        public async Task ThePrivateFleetAppsIdIsRefusedAsNotOpen()
        {
            AssertRefused(await Confirm(Workspace(), publicId: PrivatePublicId), ConfirmationOutcome.AppNotOpen);
        }

        [Test]
        public async Task ADisabledAppIsRefused()
        {
            AssertRefused(await Confirm(Workspace(d => d.App.Enabled = false)), ConfirmationOutcome.AppDisabled);
        }

        [Test]
        public async Task AnAppIdTheWorkspaceDoesNotHaveIsRefusedWithoutAskingForDetails()
        {
            FakePingCoreApi api = Workspace(listOpenApp: false);

            AssertRefused(await Confirm(api), ConfirmationOutcome.AppNotFound);
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET discovery/apps" }));
        }

        [Test]
        public async Task ADetailAnswerForAnotherPublicIdIsRefused()
        {
            AssertRefused(await Confirm(Workspace(d => d.App.PublicId = PrivatePublicId)), ConfirmationOutcome.AppNotFound);
        }

        [Test]
        public async Task TheOnlyMatchingTokenHavingAllocateScopeIsRefused()
        {
            AssertRefused(await Confirm(Workspace(d => d.Tokens[0].Scope = "allocate")), ConfirmationOutcome.NonHeartbeatTokenSameLastFour);
        }

        [Test]
        public async Task TheOnlyMatchingTokenHavingBothScopeIsRefused()
        {
            AssertRefused(await Confirm(Workspace(d => d.Tokens[0].Scope = "both")), ConfirmationOutcome.NonHeartbeatTokenSameLastFour);
        }

        [Test]
        public async Task AHeartbeatMatchBesideAnActiveAllocateMatchIsRefused()
        {
            AssertRefused(await Confirm(Workspace(d => d.Tokens[1].MaskedToken = Masked)), ConfirmationOutcome.NonHeartbeatTokenSameLastFour);
        }

        [Test]
        public async Task NoTokenEndingInTheLastFourIsRefused()
        {
            AssertRefused(await Confirm(Workspace(d => d.Tokens[0].MaskedToken = OtherMasked)), ConfirmationOutcome.NoMatchingHeartbeatToken);
        }

        [Test]
        public async Task TwoActiveHeartbeatTokensEndingInTheLastFourAreRefusedAsAmbiguous()
        {
            AssertRefused(await Confirm(Workspace(d => d.Tokens.Add(Row(Masked, "heartbeat", id: 21)))), ConfirmationOutcome.SeveralMatchingHeartbeatTokens);
        }

        [Test]
        public async Task AnInactiveMatchingHeartbeatTokenIsRefused()
        {
            AssertRefused(await Confirm(Workspace(d => d.Tokens[0].IsActive = false)), ConfirmationOutcome.NoMatchingHeartbeatToken);
        }

        [Test]
        public async Task AnInactiveAllocateTokenWithTheSameLastFourDoesNotBlockTheConfirmation()
        {
            ConfirmationResult result = await Confirm(Workspace(d =>
            {
                d.Tokens[1].MaskedToken = Masked;
                d.Tokens[1].IsActive = false;
            }));

            Assert.That(result.Outcome, Is.EqualTo(ConfirmationOutcome.Confirmed), result.ToString());
        }

        [Test]
        public async Task ATokenEqualToAStoredCredentialIsRefusedBeforeAnyCall()
        {
            FakePingCoreApi api = Workspace();
            var store = new FakeCredentialStore();
            store.Seed(CredentialTargets.UserKey(api.Endpoint.Host), Token);

            IReadOnlyList<string> stored = HeartbeatTokenConfirmationWriter.StoredSecrets(store, api.Endpoint.Host, 0);
            AssertRefused(await Confirm(api, stored: stored), ConfirmationOutcome.TokenIsStoredCredential);
            Assert.That(api.Calls, Is.Empty);
        }

        [Test]
        public void StoredSecretsReadsTheKeyAndTheConfiguredSourcesPushTokenOnly()
        {
            var store = new FakeCredentialStore();
            const string host = "studio.app.pingcore.io";
            store.Seed(CredentialTargets.UserKey(host), "key-secret-value");
            store.Seed(CredentialTargets.PushToken(host, 31), "push-secret-value");
            store.Seed(CredentialTargets.PushToken(host, 3002), "other-source-value");

            Assert.That(HeartbeatTokenConfirmationWriter.StoredSecrets(store, host, 31), Is.EquivalentTo(new[] { "key-secret-value", "push-secret-value" }));
            Assert.That(HeartbeatTokenConfirmationWriter.StoredSecrets(store, host, 0), Is.EquivalentTo(new[] { "key-secret-value" }));
        }

        [Test]
        public async Task APastedPublicIdInTheTokenFieldIsRefusedAsMalformed()
        {
            FakePingCoreApi api = Workspace();

            AssertRefused(await Confirm(api, token: OpenPublicId), ConfirmationOutcome.TokenMalformed);
            Assert.That(api.Calls, Is.Empty);
        }

        [Test]
        public async Task AnEmptyTokenIsRefusedAsMissing()
        {
            AssertRefused(await Confirm(Workspace(), token: "  "), ConfirmationOutcome.TokenMissing);
        }

        [Test]
        public async Task AMissingAppPublicIdIsRefused()
        {
            AssertRefused(await Confirm(Workspace(), publicId: string.Empty), ConfirmationOutcome.AppIdMissing);
        }

        [Test]
        public async Task AMissingDiscoveryViewPermissionIsReportedWithTheBrandPermission()
        {
            FakePingCoreApi api = Workspace();
            api.ListDiscoveryApps = () => ApiResult<DiscoveryAppListResponse>.Failure(
                FakePingCoreApi.Failure(WorkspaceRouteId.ListDiscoveryApps, PluginErrorKind.MissingBrandPermission, "Forbidden.", 403));

            ConfirmationResult result = await Confirm(api);

            AssertRefused(result, ConfirmationOutcome.ApiFailed);
            Assert.That(result.Error.Permission, Is.EqualTo("discovery.view"));
        }

        [Test]
        public async Task TheWrittenRecordMakesTheGuardAcceptTheTokenAndRejectAOneCharacterChange()
        {
            ConfirmationResult result = await Confirm(Workspace());
            Assert.That(result.Ok, Is.True, result.ToString());

            IReadOnlyList<BuildGuardConfirmation> recorded = BuildGuardConfirmationFile.Read(projectRoot);
            const string asset = "Assets/Resources/PingCoreClientSettings.asset";

            BuildGuardTokenResolution accepted = BuildGuardConfirmations.Resolve(new[] { new BuildGuardConfiguredToken(asset, Token) }, recorded);
            Assert.That(accepted.Findings, Is.Empty);
            Assert.That(accepted.Allowed, Has.Member(Token));

            string changed = Token.Substring(0, Token.Length - 1) + "e";
            BuildGuardTokenResolution rejected = BuildGuardConfirmations.Resolve(new[] { new BuildGuardConfiguredToken(asset, changed) }, recorded);
            Assert.That(rejected.Allowed, Is.Empty);
            Assert.That(rejected.Findings.Single().Code, Is.EqualTo("secret_literal"));
            Assert.That(rejected.Findings.Single().Detail, Is.EqualTo(BuildGuardConfirmations.UnconfirmedDetail));
            Assert.That(BuildGuardConfirmations.UnconfirmedDetail, Does.EndWith("confirm it on Window > PingCore, Player hosting"));
        }

        [Test]
        public async Task ConfirmingAgainReplacesTheSameAppsRecordAndKeepsOtherAppsRecords()
        {
            var other = new BuildGuardConfirmation
            {
                appPublicId = PrivatePublicId,
                tokenDigest = new string('a', 64),
                scope = "heartbeat",
                registrationMode = "open",
                confirmedAt = "2026-10-01T00:00:00Z",
            };
            var stale = new BuildGuardConfirmation
            {
                appPublicId = OpenPublicId,
                tokenDigest = new string('b', 64),
                scope = "heartbeat",
                registrationMode = "open",
                confirmedAt = "2026-10-01T00:00:00Z",
            };
            File.WriteAllText(RecordPath, UnityEngine.JsonUtility.ToJson(new BuildGuardConfirmationRecord { confirmations = new[] { other, stale } }, true));

            ConfirmationResult result = await Confirm(Workspace());

            Assert.That(result.Ok, Is.True, result.ToString());
            IReadOnlyList<BuildGuardConfirmation> recorded = BuildGuardConfirmationFile.Read(projectRoot);
            Assert.That(recorded.Select(c => c.appPublicId + "=" + c.tokenDigest), Is.EquivalentTo(new[]
            {
                PrivatePublicId + "=" + new string('a', 64),
                OpenPublicId + "=" + TokenDigest,
            }));
        }

        [Test]
        public void TheStatusLineSaysConfirmedOnlyForTheConfirmedTokenAndNeverQuotesIt()
        {
            IReadOnlyList<BuildGuardConfirmation> records = new[]
            {
                new BuildGuardConfirmation { appPublicId = OpenPublicId, tokenDigest = TokenDigest, scope = "heartbeat", registrationMode = "open", confirmedAt = "2026-10-06T12:00:00Z" },
            };

            Assert.That(ConfirmationStatus.StateOf(Token, records), Is.EqualTo(ConfirmationState.Confirmed));
            Assert.That(ConfirmationStatus.StateOf(Token.Substring(0, Token.Length - 1) + "e", records), Is.EqualTo(ConfirmationState.Unconfirmed));
            Assert.That(ConfirmationStatus.StateOf(string.Empty, records), Is.EqualTo(ConfirmationState.NoToken));
            Assert.That(ConfirmationStatus.Describe(Token, records), Does.Contain(OpenPublicId).And.Not.Contain("open" + Tail16));
            Assert.That(ConfirmationStatus.Describe(Token, Array.Empty<BuildGuardConfirmation>()), Does.Contain("secret_literal"));
        }

        [Test]
        public void TheLastFourLimitationIsStatedForTheSettingsPage()
        {
            Assert.That(HeartbeatTokenConfirmationPolicy.LastFourLimitation, Does.Contain("last four characters").And.Contain("SHA-256"));
        }
    }
}
