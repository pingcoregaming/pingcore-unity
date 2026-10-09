using System;
using System.Collections.Generic;
using System.IO;
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
    /// The runner against fakes: order of calls, persistence, the reload lock, resume, stop and refusals.
    /// </summary>
    public sealed class PipelineRunnerTests
    {
        private const string Host = "studio.app.pingcore.io";

        private string root;
        private FakePingCoreApi api;
        private FakeCredentialStore store;
        private FakeProcessRunner processes;
        private FakeBuildStep builder;
        private CountingReloadLock reloadLock;
        private List<string> log;
        private Queue<ReleaseDetailResponse> polls;
        private Func<TimeSpan, CancellationToken, Task> delay;
        private DateTime now;
        private List<(string Root, string Folder, string Executable)> folderChecks;
        private BuildFolderVerdict folderVerdict;
        private Queue<PingctlLocation> locations;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "pingcore-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            api = new FakePingCoreApi(Host)
            {
                ListBuildTargets = id => ApiResult<BuildTargetsResponse>.Success(Targets(versions: TestVersion)),
                CreateRelease = (id, body) => ApiResult<ReleaseCreatedResponse>.Success(new ReleaseCreatedResponse { Release = new ReleaseView { ReleaseId = 12, State = "pending", TargetBuildVersion = body.TargetBuildVersion } }),
            };
            polls = new Queue<ReleaseDetailResponse>(new[] { Detail(12, "rolling", null, null, Location(21, "rolling", 1, 1)), Detail(12, "completed", null, null, Location(21, "done", 0, 1)) });
            api.GetRelease = (f, r) => ApiResult<ReleaseDetailResponse>.Success(polls.Count > 1 ? polls.Dequeue() : polls.Peek());
            store = new FakeCredentialStore();
            store.Seed(CredentialTargets.PushToken(Host, 31), FakePushToken());
            processes = new FakeProcessRunner()
                .Enqueue("pingctl", new FakeProcessScript(0, "pingctl v0.1.1"))
                .Enqueue("pingctl", new FakeProcessScript(0, "auth with " + FakePushToken(), "Published version " + TestVersion));
            builder = new FakeBuildStep();
            reloadLock = new CountingReloadLock();
            log = new List<string>();
            delay = (t, c) => Task.CompletedTask;
            now = T0;
            folderChecks = new List<(string, string, string)>();
            folderVerdict = BuildFolderVerdict.Pass;
            locations = new Queue<PingctlLocation>();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private PipelineRunner Runner()
        {
            var runner = new PipelineRunner(new PipelineServices
            {
                ProjectRoot = root,
                Api = api,
                Store = store,
                Processes = processes,
                Builder = builder,
                ReloadLock = reloadLock,
                UtcNow = () => now,
                Delay = (t, c) => delay(t, c),
                LocatePingctl = () => locations.Count > 0 ? locations.Dequeue() : new PingctlLocation(@"C:\tools\pingctl.exe", PingctlLocator.SourceBundled + " 0.1.1", null),
                CheckBuildFolder = (r, f, e) =>
                {
                    folderChecks.Add((r, f, e == null ? null : string.Join(",", e.Paths) + (e.AnyOf ? " (any)" : string.Empty)));
                    return folderVerdict;
                },
                NewRunId = () => "run1",
            });
            runner.LogLine += log.Add;
            return runner;
        }

        [Test]
        public async Task BuildPushReleaseAndWatchCallEverythingInOrderAndLockReloadsOnlyAroundTheChild()
        {
            PipelineRunner runner = Runner();
            var progress = new List<ReleaseProgressView>();
            runner.ReleaseProgressChanged += progress.Add;
            PipelineOutcome outcome = await runner.RunAsync(Request(), CancellationToken.None);

            Assert.That(outcome.Status, Is.EqualTo(PipelineRunStatus.Succeeded), outcome.Message);
            Assert.That(builder.Builds, Is.EqualTo(new[] { (TestVersion, Profile, Executable) }), "the build gets the profile and the executable");
            Assert.That(folderChecks.Single(), Is.EqualTo((root, Path.GetFullPath(Path.Combine(root, "Builds/Server/" + TestVersion)), Executable)),
                "the push checks the folder the build wrote against the startup command");
            Assert.That(processes.Runs.Select(r => r.Args[0]), Is.EqualTo(new[] { "version", "push" }));
            Assert.That(api.Calls, Is.EqualTo(new[]
            {
                "GET fleets/1/build-targets", "POST fleets/1/releases", "GET fleets/1/releases/12", "GET fleets/1/releases/12",
            }));
            Assert.That(((ReleaseCreateRequest)api.Bodies.Single()).TargetBuildVersion, Is.EqualTo(TestVersion));
            Assert.That((reloadLock.Acquired, reloadLock.Released), Is.EqualTo((1, 1)));
            Assert.That(progress.Select(p => p.State), Is.EqualTo(new[] { "rolling", "completed" }));
            Assert.That(runner.Steps.Select(s => s.Status), Is.EqualTo(new[]
            {
                PipelineStepStatus.Succeeded, PipelineStepStatus.Succeeded, PipelineStepStatus.Succeeded, PipelineStepStatus.Succeeded,
                PipelineStepStatus.Succeeded, PipelineStepStatus.Succeeded, PipelineStepStatus.Skipped,
            }));
            Assert.That(DeployStateFile.Load(root).State.Phase, Is.EqualTo(DeployPhase.Done));
            Assert.That(runner.ResumeOffer, Is.Null);
        }

        [Test]
        public async Task NoLogLineOrSavedStateEverHoldsThePushToken()
        {
            PipelineRunner runner = Runner();
            await runner.RunAsync(Request(), CancellationToken.None);
            Assert.That(log, Has.Some.Contains("pingctl:"), "the child's output reached the log");
            Assert.That(log.Any(l => l.Contains(FakePushToken())), Is.False, "[mutation: log the child's raw output]");
            Assert.That(File.ReadAllText(DeployStateFile.PathIn(root)).Contains(FakePushToken()), Is.False);
        }

        [Test]
        public async Task TheReloadLockIsReleasedWhenTheChildFails()
        {
            processes = new FakeProcessRunner().Enqueue("pingctl", new FakeProcessScript(0, "pingctl v0.1.1")).Enqueue("pingctl", new FakeProcessScript(1).Err("refused"));
            PipelineOutcome outcome = await Runner().RunAsync(Request(), CancellationToken.None);
            Assert.That(outcome.Status, Is.EqualTo(PipelineRunStatus.Failed));
            Assert.That(outcome.Failure, Is.EqualTo(DeployFailure.PushFailed));
            Assert.That((reloadLock.Acquired, reloadLock.Released), Is.EqualTo((1, 1)), "[mutation: unlock only on success]");
            Assert.That(api.Calls, Is.Empty, "nothing is released after a failed push");
        }

        [Test]
        public async Task ASecondStartWhileAStepRunsIsAnsweredWithTheReasonAndAStopKeepsTheReleaseResumable()
        {
            var gate = new TaskCompletionSource<bool>();
            delay = async (t, c) =>
            {
                using (c.Register(() => gate.TrySetCanceled()))
                {
                    await gate.Task;
                }
            };
            PipelineRunner runner = Runner();
            Task<PipelineOutcome> first = runner.RunAsync(Request(), CancellationToken.None);
            Assert.That(runner.IsRunning, Is.True, "the run waits for its first poll");

            PipelineOutcome second = await runner.RunAsync(Request(), CancellationToken.None);
            Assert.That(second.Refused, Is.True, "[mutation: queue or start a second run]");
            Assert.That(second.Message, Does.Contain("already running"));

            runner.Stop();
            PipelineOutcome stopped = await first;
            Assert.That(stopped.Status, Is.EqualTo(PipelineRunStatus.Cancelled));
            Assert.That(runner.State.Phase, Is.EqualTo(DeployPhase.Running), "a stop never fails the run");
            Assert.That(runner.State.ReleaseUnfinished, Is.True);
            Assert.That(runner.ResumeOffer.Message, Is.EqualTo($"Release 12 on fleet 1 ({TestVersion}) is still running; resume watching?"));
            Assert.That(runner.Steps.Single(s => s.Step == PipelineStep.Watch).Status, Is.EqualTo(PipelineStepStatus.Cancelled));
        }

        [Test]
        public async Task AfterAReloadARunnerOffersTheSavedReleaseAndWatchesItWithoutReleasingAgain()
        {
            await Runner().RunAsync(Request(), CancellationToken.None);
            DeployState saved = DeployStateFile.Load(root).State;
            saved.Phase = DeployPhase.Running;
            saved.ReleaseState = "rolling";
            saved.InFlight = DeployActionKind.PollRelease;
            DeployStateFile.Save(root, saved);
            api.Calls.Clear();
            polls = new Queue<ReleaseDetailResponse>(new[] { Detail(12, "completed") });

            PipelineRunner afterReload = Runner();
            Assert.That(afterReload.RunStatus, Is.EqualTo(PipelineRunStatus.CanResume));
            Assert.That(afterReload.ResumeOffer.ReleaseId, Is.EqualTo(12));
            PipelineOutcome refused = await afterReload.RunAsync(Request(), CancellationToken.None);
            Assert.That(refused.Refused, Is.True, "a new run waits until the saved release is resumed or discarded");
            store.Operations.Clear();
            Assert.That(afterReload.CheckPush(Request(build: false, release: false)), Does.Contain("Release 12 on fleet 1 may still be running"),
                "[mutation: CheckPush passes a push the run then refuses, after a token was issued for it]");
            Assert.That(store.Operations, Is.Empty);
            Assert.That(api.Calls, Is.Empty);

            PipelineOutcome resumed = await afterReload.ContinueAsync(Request(), CancellationToken.None);
            Assert.That(resumed.Status, Is.EqualTo(PipelineRunStatus.Succeeded), resumed.Message);
            Assert.That(api.Calls, Is.EqualTo(new[] { "GET fleets/1/releases/12" }), "[mutation: plan from scratch on resume]");
        }

        [Test]
        public async Task WithoutAPushTokenTheRunWaitsThenContinuesOnceOneIsIssued()
        {
            store = new FakeCredentialStore();
            api.IssuePushToken = id => (FakePushToken(), "now", null);
            PipelineRunner runner = Runner();
            PipelineOutcome waiting = await runner.RunAsync(Request(release: false), CancellationToken.None);
            Assert.That(waiting.Status, Is.EqualTo(PipelineRunStatus.NeedsInput));
            Assert.That(runner.Steps.Single(s => s.Step == PipelineStep.PushToken).Status, Is.EqualTo(PipelineStepStatus.NeedsInput));
            Assert.That(processes.Runs, Is.Empty);
            Assert.That(api.Calls, Is.Empty, "the pipeline itself never issues a token");

            Assert.That((await runner.PushTokens.IssueAsync(api, 31, true, false, CancellationToken.None)).Ok, Is.True);
            PipelineOutcome done = await runner.ContinueAsync(Request(release: false), CancellationToken.None);
            Assert.That(done.Status, Is.EqualTo(PipelineRunStatus.Succeeded), done.Message);
            Assert.That(builder.Builds.Count, Is.EqualTo(1), "the build is not run twice");
            Assert.That(runner.State.Snapshot, Is.EqualTo(TestVersion));
        }

        [Test]
        public async Task ABuildThatSwitchedTheEditorBackFinishesItsRowAndNothingContinuesByItself()
        {
            builder.ReloadFollows = true;
            PipelineRunner runner = Runner();
            PipelineOutcome outcome = await runner.RunAsync(Request(push: false, release: false), CancellationToken.None);
            Assert.That(outcome.Status, Is.EqualTo(PipelineRunStatus.Succeeded), outcome.Message);
            Assert.That(log, Has.Some.Contains("switching the Editor back"));
            Assert.That(Runner().ResumeOffer, Is.Null, "a finished build leaves nothing to continue after the reload");
        }

        [Test]
        public async Task APushOfAFolderWithoutTheStartupExecutableNeverStartsPingctl()
        {
            folderVerdict = new BuildFolderVerdict(new[] { "Your game launches ./BeaconRush.x86_64; this build has no such file." }, DeployFailure.StartupExecutableMissing);
            PipelineOutcome outcome = await Runner().RunAsync(Request(build: false, release: false), CancellationToken.None);
            Assert.That(outcome.Failure, Is.EqualTo(DeployFailure.StartupExecutableMissing));
            Assert.That(outcome.Message, Is.EqualTo("Your game launches ./BeaconRush.x86_64; this build has no such file."));
            Assert.That(processes.Runs, Is.Empty, "[mutation: check the folder after pingctl ran]");
            Assert.That(folderChecks.Single().Folder, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "Builds/Server/" + TestVersion))), "the chosen folder is the one checked");
        }

        [Test]
        public async Task APushWhoseStartupCheckWasSkippedRunsWithTheGuardCheckAloneAndOnlyWithAReason()
        {
            DeployRequest skipped = Request(build: false, release: false);
            skipped.Startup = null;
            skipped.StartupCheckSkipped = StartupCheck.SkippedPrefix + "no deployment of the fleet names a process to launch.";
            PipelineOutcome outcome = await Runner().RunAsync(skipped, CancellationToken.None);
            Assert.That(outcome.Status, Is.EqualTo(PipelineRunStatus.Succeeded), outcome.Message);
            Assert.That(folderChecks.Single().Executable, Is.Null, "the folder check still runs (the guard's verdict), without the file check");
            Assert.That(processes.Runs.Select(r => r.Args[0]), Is.EqualTo(new[] { "version", "push" }), "[mutation: refuse a push whose startup check was skipped]");
            Assert.That(DeployStateFile.Load(root).State.StartupCheckSkipped, Is.EqualTo(skipped.StartupCheckSkipped), "a resumed push keeps the reason");

            DeployRequest noReason = Request(build: false, release: false);
            noReason.Startup = null;
            Assert.That(Runner().CheckPush(noReason), Does.Contain("startup command is not known"), "no executable and no reason is still refused");
        }

        [Test]
        public void CheckPushRefusesWhatThePushWouldRefuseWithoutRunningOrCallingAnything()
        {
            DeployRequest request = Request(build: false, release: false);
            folderVerdict = new BuildFolderVerdict(new[] { "Your game launches ./BeaconRush.x86_64; this build has no such file." }, DeployFailure.StartupExecutableMissing);
            Assert.That(Runner().CheckPush(request), Is.EqualTo("Your game launches ./BeaconRush.x86_64; this build has no such file."), "[mutation: issue a token before the folder check]");
            Assert.That(folderChecks.Single(), Is.EqualTo((root, Path.GetFullPath(Path.Combine(root, "Builds/Server/" + TestVersion)), Executable)));

            folderVerdict = BuildFolderVerdict.Pass;
            locations.Enqueue(new PingctlLocation(null, null, "the bundled pingctl for windows-amd64 does not match its pinned SHA-256, so it is not run."));
            Assert.That(Runner().CheckPush(request), Does.Contain("does not match its pinned SHA-256"));
            Assert.That(Runner().CheckPush(request), Is.Null, "a folder that passes and a pingctl that may run");

            request.PushFolder = null;
            Assert.That(Runner().CheckPush(request), Is.EqualTo("Choose the folder to push, or build first."));
            Assert.That(processes.Runs, Is.Empty);
            Assert.That(api.Calls, Is.Empty);
            Assert.That(store.Operations, Is.Empty, "nothing is read or issued");
        }

        // A bundled location for a real file under the scratch root, carrying the digest of `checkedBytes` (computed with
        // the platform's SHA-256, not the code under test).
        private PingctlLocation PlantBundled(byte[] checkedBytes, byte[] onDisk)
        {
            string binary = Path.Combine(root, "pkg", "pingctl.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary));
            File.WriteAllBytes(binary, onDisk);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return new PingctlLocation(binary, PingctlLocator.SourceBundled + " 0.1.1", null, string.Concat(sha.ComputeHash(checkedBytes).Select(b => b.ToString("x2"))));
            }
        }

        [Test]
        public async Task TheBundledPingctlRunsOnlyAsItsCheckedPrivateCopyWhichIsDeletedAfterThePush()
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes("pretend pingctl");
            PingctlLocation bundled = PlantBundled(bytes, bytes);
            locations.Enqueue(bundled);
            locations.Enqueue(bundled);
            PipelineOutcome outcome = await Runner().RunAsync(Request(build: false, release: false), CancellationToken.None);
            Assert.That(outcome.Status, Is.EqualTo(PipelineRunStatus.Succeeded), outcome.Message);

            string runRoot = Path.Combine(root, "Library", "PingCore", "pingctl-run");
            string ran = processes.Runs.Select(r => r.FileName).Distinct().Single();
            Assert.That(ran, Is.Not.EqualTo(bundled.Path), "[mutation: run the package's binary, checked earlier, in place]");
            Assert.That(ran, Does.StartWith(runRoot + Path.DirectorySeparatorChar), "version and push both ran the run copy");
            Assert.That(Directory.GetDirectories(runRoot), Is.Empty, "the copy is gone once the push ends");
            Assert.That(log, Has.Some.Contains("checked run copy"));
        }

        [Test]
        public async Task ABundledPingctlThatChangedAfterItsCheckIsNeverRun()
        {
            // The locator checked the bytes it saw; the file on disk is something else by the time the push starts.
            PingctlLocation bundled = PlantBundled(System.Text.Encoding.ASCII.GetBytes("pretend pingctl"), System.Text.Encoding.ASCII.GetBytes("swapped in later"));
            locations.Enqueue(bundled);
            locations.Enqueue(bundled);
            PipelineOutcome outcome = await Runner().RunAsync(Request(build: false, release: false), CancellationToken.None);
            Assert.That(outcome.Status, Is.EqualTo(PipelineRunStatus.Failed));
            Assert.That(outcome.Message, Does.Contain("does not match its pinned SHA-256"));
            Assert.That(processes.Runs, Is.Empty, "[mutation: run the binary checked once, earlier]");
        }

        [Test]
        public async Task APushLogsTheFolderItsFileCountAndSizeFirst()
        {
            string folder = Path.Combine(root, "Builds", "Server", TestVersion);
            Directory.CreateDirectory(Path.Combine(folder, "BeaconRush_BurstDebugInformation_DoNotShip"));
            File.WriteAllBytes(Path.Combine(folder, Executable), new byte[2048]);
            File.WriteAllBytes(Path.Combine(folder, "BeaconRush_BurstDebugInformation_DoNotShip", "x.pdb"), new byte[4096]);
            PipelineOutcome outcome = await Runner().RunAsync(Request(build: false, release: false), CancellationToken.None);
            Assert.That(outcome.Status, Is.EqualTo(PipelineRunStatus.Succeeded), outcome.Message);
            string line = log.First(l => l.StartsWith("push: ", StringComparison.Ordinal));
            Assert.That(line, Does.Contain("1 file, 2 KB").And.Contain("leaving out BeaconRush_BurstDebugInformation_DoNotShip"));
        }

        [Test]
        public async Task AFailedReleaseIsAcknowledgedByTheWatch()
        {
            polls = new Queue<ReleaseDetailResponse>(new[] { Detail(12, "failed", null, "surge_timeout") });
            api.AcknowledgeRelease = (f, r) => ApiResult<ReleaseChangedResponse>.Success(new ReleaseChangedResponse { Release = new ReleaseView { ReleaseId = 12, State = "failed", AcknowledgedAt = "now" } });
            PipelineRunner runner = Runner();
            PipelineOutcome outcome = await runner.RunAsync(Request(), CancellationToken.None);
            Assert.That(outcome.Failure, Is.EqualTo(DeployFailure.ReleaseFailed));
            Assert.That(api.Calls.Last(), Is.EqualTo("POST fleets/1/releases/12/acknowledge"));
            Assert.That(runner.Steps.Single(s => s.Step == PipelineStep.Acknowledge).Status, Is.EqualTo(PipelineStepStatus.Succeeded));
            Assert.That(runner.Steps.Single(s => s.Step == PipelineStep.Watch).Status, Is.EqualTo(PipelineStepStatus.Failed));
        }

        [Test]
        public async Task CancelReleaseCallsTheWorkspaceForTheRunsRelease()
        {
            api.CancelRelease = (f, r) => ApiResult<ReleaseChangedResponse>.Success(new ReleaseChangedResponse { Release = new ReleaseView { ReleaseId = r, State = "dismissed" } });
            PipelineRunner runner = Runner();
            Assert.That((await runner.CancelReleaseAsync(CancellationToken.None)).Kind, Is.EqualTo(PluginErrorKind.Refused), "no release yet");
            await runner.RunAsync(Request(), CancellationToken.None);
            Assert.That(await runner.CancelReleaseAsync(CancellationToken.None), Is.Null);
            Assert.That(api.Calls.Last(), Is.EqualTo("POST fleets/1/releases/12/cancel"));
        }

        [Test]
        public async Task AnInvalidRequestIsRefusedWithoutTouchingAnything()
        {
            DeployRequest bad = Request();
            bad.FleetId = 0;
            PipelineOutcome outcome = await Runner().RunAsync(bad, CancellationToken.None);
            Assert.That(outcome.Refused, Is.True);
            Assert.That(builder.Builds, Is.Empty);
            Assert.That(File.Exists(DeployStateFile.PathIn(root)), Is.False);
        }

        private sealed class FakeBuildStep : IServerBuildStep
        {
            public List<(string Version, string Profile, string Executable)> Builds { get; } = new List<(string, string, string)>();

            public bool ReloadFollows { get; set; }

            public ServerBuildOutcome Build(string version, string buildProfile, string executable)
            {
                Builds.Add((version, buildProfile, executable));
                return new ServerBuildOutcome(true, "Builds/Server/" + version, null,
                    ReloadFollows ? "The build left the Editor on StandaloneLinux64 (Server); switching the Editor back to StandaloneWindows64 (Player)." : null, ReloadFollows);
            }
        }

        private sealed class CountingReloadLock : IReloadLock
        {
            public int Acquired { get; private set; }

            public int Released { get; private set; }

            public IDisposable Acquire()
            {
                Acquired++;
                return new Handle(this);
            }

            private sealed class Handle : IDisposable
            {
                private readonly CountingReloadLock owner;
                private bool done;

                public Handle(CountingReloadLock owner) => this.owner = owner;

                public void Dispose()
                {
                    if (!done)
                    {
                        done = true;
                        owner.Released++;
                    }
                }
            }
        }
    }
}
