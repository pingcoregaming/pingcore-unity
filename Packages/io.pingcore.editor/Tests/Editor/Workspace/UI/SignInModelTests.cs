using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.Tests.Credentials;
using PingCore.Editor.Workspace.Tests.Fakes;
using PingCore.Editor.Workspace.UI.Connect;
using UnityEngine;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// Connect's sign-in logic: sign-in through the section's model with a planted fake key and
    /// no workspace URL, then a scan that the key is nowhere but the credential store: not in the
    /// scratch project's <c>UserSettings/</c>, <c>ProjectSettings/</c> or <c>Library/</c>, not in the
    /// open project's, and not in any field of the model. The endpoint is the default unless the
    /// user settings override it, and a refused override signs nobody in.
    /// </summary>
    public sealed class SignInModelTests
    {
        private const string Host = "app.pingcore.io";

        private string scratch;

        private static string NewKey() => "usr" + "_" + Guid.NewGuid().ToString("N").Substring(0, 20) + "Pq9Z";

        [SetUp]
        public void SetUp()
        {
            scratch = Path.Combine(Path.GetTempPath(), "pingcore-signin-" + Guid.NewGuid().ToString("N"));
            foreach (string dir in new[] { "Assets", "ProjectSettings", "UserSettings", "Library" })
            {
                Directory.CreateDirectory(Path.Combine(scratch, dir));
            }

            File.WriteAllText(Path.Combine(scratch, "Library", "marker.txt"), "scratch");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }
        }

        private static (SignInModel Model, FakeCredentialStore Persistent, FakeCredentialStore Session, List<FakePingCoreApi> Apis) Build(string root, string key)
        {
            var persistent = new FakeCredentialStore(CredentialStoreKind.WindowsCredentialManager, "os store");
            var session = new FakeCredentialStore(CredentialStoreKind.Session, "session");
            var apis = new List<FakePingCoreApi>();
            var model = new SignInModel(root, sessionOnly => sessionOnly ? session : persistent, (endpoint, keys) =>
            {
                var api = new FakePingCoreApi(endpoint.Host)
                {
                    ListFleets = () => keys.Read(CredentialTargets.UserKey(endpoint.Host))?.Secret == key
                        ? ApiResult<FleetListResponse>.Success(new FleetListResponse { Fleets = new List<FleetView> { new FleetView { FleetId = 1, Name = "Beacon Rush", Status = "active" } } })
                        : ApiResult<FleetListResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.ListFleets, PluginErrorKind.NotSignedIn, "Unauthorized.", 401)),
                    GetCapabilities = () => ApiResult<CapabilitiesResponse>.Success(new CapabilitiesResponse
                    {
                        Identity = new CapabilitiesIdentity { UserId = 9, Persona = "workspace", IsBrandMember = true, IsOwner = false, BrandId = 3, BrandName = "Acme Studio" },
                        Permissions = new List<string> { "fleets.view" },
                    }),
                };
                apis.Add(api);
                return api;
            });
            return (model, persistent, session, apis);
        }

        [Test]
        public async Task SigningInWithNoUrlKeepsTheKeyOnlyInTheStoreAndNamesTheWorkspace()
        {
            string key = NewKey();
            var (model, persistent, _, apis) = Build(scratch, key);

            Assert.That(model.Endpoint, Is.SameAs(WorkspaceEndpoint.Default), "no override: the default endpoint");
            Assert.That(model.OverrideNote, Is.Null);
            SignInResult result = await model.SignInAsync(key, CancellationToken.None);
            model.SetPingctlPath("C:/tools/pingctl.exe");
            model.SetSessionOnly(true);
            model.SetSessionOnly(false);
            SignInResult verified = await model.VerifyAsync(CancellationToken.None);

            Assert.That(result.Ok, Is.True, result.Error?.ToString());
            Assert.That(verified.Ok, Is.True, verified.Error?.ToString());
            Assert.That(apis.Select(a => a.Endpoint.ApiBase).Distinct(), Is.EqualTo(new[] { "https://app.pingcore.io/api/" }));
            Assert.That(persistent.Read(CredentialTargets.UserKey(Host)).Secret, Is.EqualTo(key), "the store holds the key");
            Assert.That(model.StoredKeyMask(), Is.EqualTo("usr_...Pq9Z"));
            Assert.That(SignInModel.Describe(verified), Is.EqualTo("Signed in to Acme Studio as usr_...Pq9Z, a brand member. Fleets: Beacon Rush (#1, active)."));
            Assert.That(File.Exists(EditorProjectSettings.PathIn(scratch)), Is.False, "sign-in writes no project settings: there is no workspace host to record");

            Assert.That(ProjectSecretScan.FilesContaining(scratch, key, Array.Empty<string>()), Is.Empty, "the scratch project holds the key");
            string open = Path.GetDirectoryName(Application.dataPath);
            var openHits = new[] { "UserSettings", "ProjectSettings", "Library" }
                .Select(d => Path.Combine(open, d))
                .Where(Directory.Exists)
                .SelectMany(d => ProjectSecretScan.FilesContaining(d, key, ProjectSecretScan.CacheFolders.Select(c => Path.Combine("..", c))))
                .ToList();
            Assert.That(openHits, Is.Empty, "files in the open project holding the planted key:\n" + string.Join("\n", openHits));

            Assert.That(StringFieldsHolding(model, key), Is.Empty, "the model keeps no copy of the key");
            Assert.That(model.SignOut(), Is.EqualTo("Signed out; the key was deleted from the credential store."));
            Assert.That(persistent.Read(CredentialTargets.UserKey(Host)), Is.Null);
        }

        [Test]
        public async Task TheSignedInCheckAsksTheStoreWithoutReadingTheKeyAndOnlyA401IsARefusal()
        {
            string key = NewKey();
            var (model, persistent, _, _) = Build(scratch, key);
            Assert.That(model.HasStoredKey(), Is.False);
            await model.SignInAsync(key, CancellationToken.None);

            persistent.Operations.Clear();
            Assert.That(model.HasStoredKey(), Is.True);
            Assert.That(persistent.Operations, Is.EqualTo(new[] { "exists " + CredentialTargets.UserKey(Host) }), "asked with Exists, never Read");
            Assert.That(model.KeyRefused, Is.False);

            persistent.FailNextWith("store busy");
            await model.VerifyAsync(CancellationToken.None);
            Assert.That((model.LastResult.Ok, model.LastResult.Error.Kind), Is.EqualTo((false, PluginErrorKind.NotSignedIn)));
            Assert.That(model.KeyRefused, Is.False, "a store that could not be read is not a refused key");

            persistent.Seed(CredentialTargets.UserKey(Host), "usr" + "_" + "revokedkeyvalue1234567");
            await model.VerifyAsync(CancellationToken.None);
            Assert.That(model.KeyRefused, Is.True, "the workspace answered 401 for the stored key");
            Assert.That(model.HasStoredKey(), Is.True, "control: the key is still stored; only the refusal reopens Connect");
        }

        [Test]
        public async Task AKeyTheWorkspaceRefusesIsNeitherStoredNorNamed()
        {
            string key = NewKey();
            var (model, persistent, _, apis) = Build(scratch, "usr" + "_" + "someotherkeyvalue12345");

            SignInResult result = await model.SignInAsync(key, CancellationToken.None);

            Assert.That(result.Ok, Is.False);
            Assert.That(persistent.Targets, Is.Empty);
            Assert.That(apis.Single().Calls, Is.EqualTo(new[] { "GET fleets" }), "[mutation: read the name before verifying] a refused key asks nothing more");
            Assert.That(ProjectSecretScan.FilesContaining(scratch, key, Array.Empty<string>()), Is.Empty);
            Assert.That(SignInModel.Describe(result), Does.Contain("Unauthorized."));
        }

        [Test]
        public async Task AnOverrideInTheUserSettingsMovesEveryCallAndIsShown()
        {
            new EditorUserSettings { ApiBaseOverride = "https://api.example.test/api" }.Save(scratch);
            string key = NewKey();
            var (model, persistent, _, apis) = Build(scratch, key);

            Assert.That(model.Endpoint.Host, Is.EqualTo("api.example.test"));
            Assert.That(model.OverrideNote, Does.Contain("https://api.example.test/api/").And.Contain("apiBaseOverride"));
            Assert.That((await model.SignInAsync(key, CancellationToken.None)).Ok, Is.True);
            Assert.That(apis.Single().Endpoint.Host, Is.EqualTo("api.example.test"));
            Assert.That(persistent.Read(CredentialTargets.UserKey("api.example.test")).Secret, Is.EqualTo(key));
            Assert.That(persistent.Read(CredentialTargets.UserKey(Host)), Is.Null, "[mutation: ignore the override] app.pingcore.io's key is untouched");
        }

        [Test]
        public async Task ARefusedOverrideSignsNobodyInAndCallsNothing()
        {
            Directory.CreateDirectory(Path.Combine(scratch, "UserSettings"));
            File.WriteAllText(EditorUserSettings.PathIn(scratch), "{\"format\":\"pingcore-editor-user/1\",\"apiBaseOverride\":\"http://api.example.test\"}");
            string key = NewKey();
            var (model, persistent, _, apis) = Build(scratch, key);

            Assert.That(model.Endpoint, Is.Null);
            Assert.That(model.EndpointProblem, Does.Contain("https"));
            SignInResult result = await model.SignInAsync(key, CancellationToken.None);

            Assert.That(result.Ok, Is.False);
            Assert.That(apis, Is.Empty, "[mutation: fall back to the default] nothing is sent anywhere");
            Assert.That(persistent.Targets, Is.Empty);
        }

        [Test]
        public async Task SessionOnlyWritesTheKeyToTheSessionStoreAndTheChoiceToUserSettings()
        {
            string key = NewKey();
            var (model, persistent, session, _) = Build(scratch, key);
            model.SetSessionOnly(true);

            await model.SignInAsync(key, CancellationToken.None);

            Assert.That(session.Read(CredentialTargets.UserKey(Host)).Secret, Is.EqualTo(key));
            Assert.That(persistent.Targets, Is.Empty);
            Assert.That(EditorUserSettings.Load(scratch).SessionOnlyKey, Is.True);
            Assert.That(model.StoreBanner().Warning, Is.False);
        }

        [Test]
        public async Task SignOutDeletesTheKeyFromThePersistentAndTheSessionStore()
        {
            string key = NewKey();
            var (model, persistent, session, _) = Build(scratch, key);
            await model.SignInAsync(key, CancellationToken.None);
            session.Seed(CredentialTargets.UserKey(Host), key);
            Assert.That(persistent.Read(CredentialTargets.UserKey(Host)), Is.Not.Null, "the control: both stores hold a key");

            Assert.That(model.SignOut(), Is.EqualTo("Signed out; the key was deleted from the credential store."));

            Assert.That(persistent.Read(CredentialTargets.UserKey(Host)), Is.Null);
            Assert.That(session.Read(CredentialTargets.UserKey(Host)), Is.Null, "[mutation: delete from the chosen store only]");
        }

        [Test]
        public async Task EverySignInProbesTheStoreAgain()
        {
            string key = NewKey();
            var persistent = new FakeCredentialStore(CredentialStoreKind.WindowsCredentialManager, "os store");
            int probes = 0;
            var model = new SignInModel(scratch, s => persistent, (endpoint, keys) => new FakePingCoreApi(endpoint.Host)
            {
                ListFleets = () => ApiResult<FleetListResponse>.Success(new FleetListResponse { Fleets = new List<FleetView>() }),
                GetCapabilities = () => ApiResult<CapabilitiesResponse>.Success(new CapabilitiesResponse { Permissions = new List<string>() }),
            }, () => probes++);

            await model.SignInAsync(key, CancellationToken.None);
            await model.SignInAsync(key, CancellationToken.None);

            Assert.That(probes, Is.EqualTo(2), "[mutation: cache the store choice for the session] every sign-in probes the store again");
        }

        [Test]
        public void TheEditorPrefsFallbackShowsAWarningBanner()
        {
            var fallback = new FakeCredentialStore(CredentialStoreKind.EditorPrefs, "Stored in EditorPrefs, not the OS credential store: no store.");
            var model = new SignInModel(scratch, _ => fallback, (e, k) => new FakePingCoreApi(e.Host));

            (string text, bool warning) = model.StoreBanner();

            Assert.That(warning, Is.True);
            Assert.That(text, Does.StartWith("Stored in EditorPrefs, not the OS credential store"));
        }

        [Test]
        public void AToolPathThatLooksLikeACredentialIsRefused()
        {
            var (model, _, _, _) = Build(scratch, NewKey());

            Assert.Throws<InvalidOperationException>(() => model.SetPingctlPath("C:/x/" + "usr" + "_" + "abcdefghijklmnop1234"));
            Assert.That(File.Exists(EditorUserSettings.PathIn(scratch)), Is.False);
        }

        private static List<string> StringFieldsHolding(object target, string value)
        {
            var hits = new List<string>();
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            Walk(target, value, "model", hits, seen, 0);
            return hits;
        }

        private static void Walk(object node, string value, string path, List<string> hits, HashSet<object> seen, int depth)
        {
            if (node == null || depth > 4 || node is Delegate || !seen.Add(node))
            {
                return;
            }

            if (node is string s)
            {
                if (s.Contains(value))
                {
                    hits.Add(path);
                }

                return;
            }

            Type type = node.GetType();
            if (type.IsPrimitive || type.IsEnum || !type.Namespace?.StartsWith("PingCore", StringComparison.Ordinal) == true && !(node is System.Collections.IEnumerable))
            {
                return;
            }

            if (node is System.Collections.IEnumerable items)
            {
                int i = 0;
                foreach (object item in items)
                {
                    Walk(item, value, path + "[" + i++ + "]", hits, seen, depth + 1);
                }

                return;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Walk(field.GetValue(node), value, path + "." + field.Name, hits, seen, depth + 1);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
