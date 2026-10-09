using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.Tests.Fakes;
using PingCore.Editor.Workspace.UI.Connect;
using UnityEditor;
using UnityEngine;
using EditorUserSettings = PingCore.Editor.Workspace.Settings.EditorUserSettings;

namespace PingCore.Editor.Workspace.Tests.Credentials
{
    /// <summary>
    /// Sign-in, verification and sign-out against a fake API (no workspace URL: one endpoint, and
    /// the workspace's name from <c>GET me/capabilities</c>), and the scan that a planted fake key
    /// reaches no file under the project, and not <c>EditorPrefs</c> while the Windows store is in
    /// use, after a real sign-in and sign-out through Connect's model.
    /// </summary>
    public sealed class SignInServiceTests
    {
        private static string NewKey() => "usr_" + Guid.NewGuid().ToString("N").Substring(0, 20) + "Wx7Q";

        private static readonly WorkspaceEndpoint Studio = WorkspaceEndpoint.ForHost("studio.app.pingcore.io");

        private static (SignInService Service, FakeCredentialStore Store, List<FakePingCoreApi> Apis) Build(Func<FakePingCoreApi, string, ApiResult<FleetListResponse>> answer, Func<ApiResult<CapabilitiesResponse>> identity = null)
        {
            var store = new FakeCredentialStore();
            var apis = new List<FakePingCoreApi>();
            var service = new SignInService(store, (endpoint, keys) =>
            {
                var api = new FakePingCoreApi(endpoint.Host);
                api.ListFleets = () => answer(api, keys.Read(CredentialTargets.UserKey(endpoint.Host))?.Secret);
                api.GetCapabilities = identity ?? (() => ApiResult<CapabilitiesResponse>.Success(WorkspaceFixtures.Payload<CapabilitiesResponse>("me.capabilities.json")));
                apis.Add(api);
                return api;
            });
            return (service, store, apis);
        }

        [Test]
        public async Task AValidKeyIsVerifiedThenStoredAndShownAsPrefixAndLastFour()
        {
            string key = NewKey();
            var (service, store, apis) = Build((api, presented) => presented == key
                ? ApiResult<FleetListResponse>.Success(WorkspaceFixtures.Payload<FleetListResponse>("fleets.list.json"))
                : ApiResult<FleetListResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.ListFleets, PluginErrorKind.NotSignedIn, "bad key", 401)));

            SignInResult result = await service.SignInAsync(Studio, key, CancellationToken.None);

            Assert.That(result.Ok, Is.True, result.Error?.ToString());
            Assert.That(result.Host, Is.EqualTo("studio.app.pingcore.io"));
            Assert.That(result.MaskedKey, Is.EqualTo("usr_...Wx7Q"));
            Assert.That(result.MaskedKey, Does.Not.Contain(key.Substring(4, 8)));
            Assert.That(result.Fleets.Single().Name, Is.EqualTo("EU Matchmaking"));
            Assert.That(result.WorkspaceName, Is.EqualTo("Acme Studio"), "the workspace's name comes from GET me/capabilities");
            Assert.That(apis.Single().Calls, Is.EqualTo(new[] { "GET fleets", "GET me/capabilities" }), "verified with GET fleets first, then named");
            Assert.That(store.Read(CredentialTargets.UserKey("studio.app.pingcore.io")).Secret, Is.EqualTo(key));
            Assert.That(store.Operations.First(o => o.StartsWith("write")), Is.EqualTo("write PingCore/studio.app.pingcore.io/usr"));
        }

        [Test]
        public async Task ARefusedKeyIsNeverStored()
        {
            var (service, store, _) = Build((api, presented) => ApiResult<FleetListResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.ListFleets, PluginErrorKind.NotSignedIn, "bad key", 401)));
            SignInResult result = await service.SignInAsync(Studio, NewKey(), CancellationToken.None);
            Assert.That(result.Error.Kind, Is.EqualTo(PluginErrorKind.NotSignedIn));
            Assert.That(store.Targets, Is.Empty);
            Assert.That(store.Operations.Any(o => o.StartsWith("write")), Is.False);
        }

        [Test]
        public async Task AMalformedKeyIsRefusedBeforeAnyCall()
        {
            var (service, store, apis) = Build((api, presented) => throw new AssertionException("never called"));
            Assert.That((await service.SignInAsync(Studio, "dsc_" + new string('a', 20), CancellationToken.None)).Error.Kind, Is.EqualTo(PluginErrorKind.Refused));
            Assert.That((await service.SignInAsync(Studio, "usr_short", CancellationToken.None)).Error.Kind, Is.EqualTo(PluginErrorKind.Refused));
            Assert.That(apis, Is.Empty);
            Assert.That(store.Targets, Is.Empty);
        }

        [Test]
        public async Task SignInNeedsNoUrlAndGoesToTheDefaultEndpoint()
        {
            string key = NewKey();
            var (service, store, apis) = Build((api, presented) => presented == key
                ? ApiResult<FleetListResponse>.Success(new FleetListResponse { Fleets = new List<FleetView>() })
                : ApiResult<FleetListResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.ListFleets, PluginErrorKind.NotSignedIn, "bad key", 401)));

            SignInResult result = await service.SignInAsync(WorkspaceEndpoint.Default, key, CancellationToken.None);

            Assert.That(result.Ok, Is.True, result.Error?.ToString());
            Assert.That(apis.Single().Endpoint.ApiBase, Is.EqualTo("https://app.pingcore.io/api/"));
            Assert.That(store.Read(CredentialTargets.UserKey("app.pingcore.io")).Secret, Is.EqualTo(key), "the key is kept under the API host");
        }

        [Test]
        public async Task AFailedNameReadStillSignsInAndSaysTheNameIsUnknown()
        {
            string key = NewKey();
            var (service, store, _) = Build(
                (api, presented) => ApiResult<FleetListResponse>.Success(new FleetListResponse { Fleets = new List<FleetView>() }),
                () => ApiResult<CapabilitiesResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.GetCapabilities, PluginErrorKind.Transport, "no answer")));

            SignInResult result = await service.SignInAsync(Studio, key, CancellationToken.None);

            Assert.That(result.Ok, Is.True);
            Assert.That(result.WorkspaceName, Is.Null);
            Assert.That(SignInModel.Describe(result), Does.StartWith("Signed in as usr_...Wx7Q (the workspace's name could not be read"));
            Assert.That(store.Targets, Is.EquivalentTo(new[] { CredentialTargets.UserKey("studio.app.pingcore.io") }));
        }

        [Test]
        public async Task VerifyUsesTheStoredKeyAndSignOutDeletesIt()
        {
            string key = NewKey();
            var (service, store, _) = Build((api, presented) => presented == key
                ? ApiResult<FleetListResponse>.Success(new FleetListResponse { Fleets = new List<FleetView>() })
                : ApiResult<FleetListResponse>.Failure(FakePingCoreApi.Failure(WorkspaceRouteId.ListFleets, PluginErrorKind.NotSignedIn, "no key", 401)));

            Assert.That((await service.VerifyAsync(Studio, CancellationToken.None)).Error.Kind, Is.EqualTo(PluginErrorKind.NotSignedIn), "nothing stored yet");
            store.Seed(CredentialTargets.UserKey("studio.app.pingcore.io"), key);
            SignInResult verified = await service.VerifyAsync(Studio, CancellationToken.None);
            Assert.That(verified.Ok, Is.True, verified.Error?.ToString());
            Assert.That(verified.MaskedKey, Is.EqualTo("usr_...Wx7Q"));
            Assert.That(service.StoredKeyMask("studio.app.pingcore.io"), Is.EqualTo("usr_...Wx7Q"));

            Assert.That(service.SignOut("studio.app.pingcore.io"), Is.True);
            Assert.That(store.Targets, Is.Empty);
            Assert.That(service.SignOut("studio.app.pingcore.io"), Is.False);
        }

        [Test]
        public void TheMaskShowsThePrefixAndLastFourOnly()
        {
            Assert.That(SignInService.Mask("usr_" + "abcdefghijklmnopQRST"), Is.EqualTo("usr_...QRST"));
            Assert.That(SignInService.Mask("cdnpush_" + "abcdefghijkl"), Is.EqualTo("cdnpush_...ijkl"));
            Assert.That(SignInService.Mask("usr_abc"), Is.EqualTo("usr_..."), "too short to show four");
            Assert.That(SignInService.Mask(null), Is.Empty);
        }

        [Test]
        public void TheProjectScanFindsAPlantedKeyAsUtf8AndUtf16()
        {
            string root = Path.Combine(Path.GetTempPath(), "pingcore-scan-" + Guid.NewGuid().ToString("N"));
            string key = NewKey();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "UserSettings"));
                File.WriteAllText(Path.Combine(root, "clean.txt"), "nothing here " + key.Substring(0, 10));
                Assert.That(ProjectSecretScan.FilesContaining(root, key, Array.Empty<string>()), Is.Empty, "a prefix of the key is not a hit");

                File.WriteAllText(Path.Combine(root, "UserSettings", "leak.json"), "{\"k\":\"" + key + "\"}", new UTF8Encoding(false));
                File.WriteAllBytes(Path.Combine(root, "wide.bin"), Encoding.Unicode.GetBytes("x" + key + "y"));
                var big = new byte[(1 << 20) - 10];
                File.WriteAllBytes(Path.Combine(root, "straddle.bin"), big.Concat(Encoding.UTF8.GetBytes(key)).ToArray());
                Assert.That(ProjectSecretScan.FilesContaining(root, key, Array.Empty<string>()).Select(Path.GetFileName),
                    Is.EquivalentTo(new[] { "leak.json", "wide.bin", "straddle.bin" }));
                Assert.That(ProjectSecretScan.FilesContaining(root, key, new[] { "UserSettings" }).Select(Path.GetFileName),
                    Is.EquivalentTo(new[] { "wide.bin", "straddle.bin" }), "an excluded folder is skipped");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task APlantedKeyReachesNoProjectFileAndNoEditorPrefsAfterARealSignInAndSignOut()
        {
            string host = "w1-scan-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".pingcore.invalid";
            string key = NewKey();
            ICredentialStore persistent = CredentialStoreSelector.Select(sessionOnly: false);
            ICredentialStore session = CredentialStoreSelector.Select(sessionOnly: true);
            string prefsKey = CredentialTargets.EditorPrefsKey(CredentialTargets.UserKey(host));
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string scratch = Path.Combine(Path.GetTempPath(), "pingcore-settings-" + Guid.NewGuid().ToString("N"));
            foreach (string dir in new[] { "Assets", "ProjectSettings", "UserSettings", "Library" })
            {
                Directory.CreateDirectory(Path.Combine(scratch, dir));
            }

            // Connect's own model, over the real stores, writing the scratch project's settings files. The scratch
            // project's user settings point the endpoint at a test host, so the real stores never touch app.pingcore.io's key.
            new EditorUserSettings { ApiBaseOverride = "https://" + host }.Save(scratch);
            var model = new SignInModel(scratch, sessionOnly => sessionOnly ? session : persistent, (endpoint, keys) => new FakePingCoreApi(endpoint.Host)
            {
                ListFleets = () => ApiResult<FleetListResponse>.Success(new FleetListResponse { Fleets = new List<FleetView>() }),
                GetCapabilities = () => ApiResult<CapabilitiesResponse>.Success(new CapabilitiesResponse { Identity = new CapabilitiesIdentity { BrandName = "Scan Studio" }, Permissions = new List<string>() }),
            });
            try
            {
                Assert.That(model.Endpoint.Host, Is.EqualTo(host), "the control: the override is in use");
                SignInResult result = await model.SignInAsync(key, CancellationToken.None);
                Assert.That(result.Ok, Is.True, result.Error?.ToString());
                Debug.Log($"[PingCore] signed in to {host} as {result.MaskedKey} ({persistent.Kind})");
                model.SetPingctlPath("C:/tools/pingctl.exe");

                Assert.That(EditorUserSettings.Load(scratch).PingctlPath, Is.EqualTo("C:/tools/pingctl.exe"), "the control: the model wrote the scratch project's settings");
                Assert.That(ProjectSecretScan.FilesContaining(scratch, key, Array.Empty<string>()), Is.Empty, "the scratch project holds the key");
                if (persistent.Kind == CredentialStoreKind.WindowsCredentialManager)
                {
                    Assert.That(EditorPrefs.HasKey(prefsKey), Is.False, "[mutation: write the fallback too] the OS store is in use, so the key never reaches EditorPrefs");
                }

                Assert.That(model.SignOut(), Does.StartWith("Signed out"));
                Assert.That(persistent.Read(CredentialTargets.UserKey(host)), Is.Null);
                Assert.That(session.Read(CredentialTargets.UserKey(host)), Is.Null);
                Assert.That(EditorPrefs.HasKey(prefsKey), Is.False, "nothing is left in EditorPrefs after sign-out, whichever store held the key");
            }
            finally
            {
                model.SignOut();
                EditorPrefs.DeleteKey(prefsKey);
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, true);
                }
            }

            var hits = ProjectSecretScan.FilesContaining(projectRoot, key, ProjectSecretScan.CacheFolders).ToList();
            string editorLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log");
            if (File.Exists(editorLog) && ProjectSecretScan.FileContains(editorLog, key))
            {
                hits.Add(editorLog);
            }

            Assert.That(hits, Is.Empty, "files holding the planted key:\n" + string.Join("\n", hits));
        }
    }

    /// <summary>Finds files that hold a value as UTF-8 or UTF-16LE, streamed in chunks. Never prints the value.</summary>
    internal static class ProjectSecretScan
    {
        /// <summary>Unity's own caches of package sources and build artefacts, which only ever hold compiled or imported project files.</summary>
        public static readonly string[] CacheFolders = { "Library/PackageCache", "Library/Bee", "Library/Artifacts", "Library/ScriptAssemblies", "Library/BurstCache", "Temp" };

        private const int Chunk = 1 << 20;

        public static IEnumerable<string> FilesContaining(string root, string value, IEnumerable<string> excluded)
        {
            var skip = new HashSet<string>(excluded.Select(e => Path.GetFullPath(Path.Combine(root, e)).TrimEnd(Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                if (skip.Contains(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)))
                {
                    continue;
                }

                string[] files;
                string[] dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
                {
                    continue;
                }

                foreach (string sub in dirs)
                {
                    pending.Push(sub);
                }

                foreach (string file in files)
                {
                    if (FileContains(file, value))
                    {
                        yield return file;
                    }
                }
            }
        }

        public static bool FileContains(string path, string value)
        {
            byte[][] needles = { Encoding.UTF8.GetBytes(value), Encoding.Unicode.GetBytes(value) };
            int overlap = needles.Max(n => n.Length) - 1;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[Chunk + overlap];
                    int carried = 0;
                    while (true)
                    {
                        int read = stream.Read(buffer, carried, Chunk);
                        if (read <= 0)
                        {
                            return false;
                        }

                        int length = carried + read;
                        if (needles.Any(n => IndexOf(buffer, length, n) >= 0))
                        {
                            return true;
                        }

                        carried = Math.Min(overlap, length);
                        Buffer.BlockCopy(buffer, length - carried, buffer, 0, carried);
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                return false;
            }
        }

        private static int IndexOf(byte[] haystack, int length, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                {
                    j++;
                }

                if (j == needle.Length)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
