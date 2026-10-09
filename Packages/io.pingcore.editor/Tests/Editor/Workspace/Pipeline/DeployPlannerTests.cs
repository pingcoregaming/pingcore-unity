using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Settings;
using static PingCore.Editor.Workspace.Tests.Pipeline.PipelineFixtures;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>
    /// The planner's transition table. Each negative names the mutation that would make it fail
    /// (in brackets), so a test that cannot fail is visible.
    /// </summary>
    public sealed class DeployPlannerTests
    {
        private static StepResult Pushed(string snapshot, bool? changed = true) => new StepResult(DeployActionKind.Push, null) { Snapshot = snapshot, SnapshotChanged = changed, AtUtc = T0 };

        private static StepResult Built() => new StepResult(DeployActionKind.Build, null) { BuildFolder = "Builds/Server/" + TestVersion, AtUtc = T0 };

        private static StepResult Listed(BuildTargetsResponse targets, DateTime at) => new StepResult(DeployActionKind.ReadBuildTargets, null) { BuildTargets = targets, AtUtc = at };

        private static StepResult Created(long id, string state = "pending") => new StepResult(DeployActionKind.StartRelease, null) { CreatedRelease = new ReleaseView { ReleaseId = id, State = state }, AtUtc = T0 };

        private static StepResult Polled(ReleaseDetailResponse detail) => new StepResult(DeployActionKind.PollRelease, null) { ReleaseDetail = detail, AtUtc = T0 };

        /// <summary>A state just past a confirmed push, ready to release <see cref="PipelineFixtures.TestVersion"/>.</summary>
        private static DeployState Confirmed(DeployRequest request, string pinned = "2026.10.06-p2fix")
        {
            (List<DeployAction> _, DeployState s) = Drive(request, Start(request), Built(), Pushed(TestVersion), Listed(Targets(pinned: pinned, versions: TestVersion), T0));
            return s;
        }

        [Test]
        public void BuildPushReleaseAndWatchRunInOrderWithTheProvenWaits()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request),
                Built(),
                Pushed(TestVersion),
                Listed(Targets(versions: TestVersion), T0),
                Created(12),
                Polled(Detail(12, "surging", null, null, Location(21, "surging", 1, 0))),
                Polled(Detail(12, "completed", null, null, Location(21, "done", 0, 1))));

            Assert.That(actions.Select(a => a.Kind), Is.EqualTo(new[]
            {
                DeployActionKind.Build, DeployActionKind.Push, DeployActionKind.ReadBuildTargets, DeployActionKind.StartRelease,
                DeployActionKind.PollRelease, DeployActionKind.PollRelease, DeployActionKind.Done,
            }));
            Assert.That(actions[4].After, Is.EqualTo(TimeSpan.FromSeconds(5)), "the first poll waits the poll interval");
            Assert.That(actions[5].After, Is.EqualTo(TimeSpan.FromSeconds(5)), "a running release is polled every 5 s");
            Assert.That(s.Phase, Is.EqualTo(DeployPhase.Done));
            Assert.That(s.ReleaseId, Is.EqualTo(12));
            Assert.That(s.Locations.Single().ToString(), Is.EqualTo("deployment 21: done, 0 old, 1 new, 0 retiring"));
        }

        [Test]
        public void WithNoPushTokenStoredThePipelineStopsForTheDeveloperThenPushesOnceOneIs()
        {
            DeployRequest request = Request(tokenStored: false);
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request), Built());
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.IssuePushTokenNeeded));
            Assert.That(s.Phase, Is.EqualTo(DeployPhase.NeedsInput));
            Assert.That(actions.Last().Message, Does.Contain("31"));

            request.PushTokenStored = true;
            DeployPlan next = DeployPlanner.Next(request, s, null, T0);
            Assert.That(next.Action.Kind, Is.EqualTo(DeployActionKind.Push));
            Assert.That(next.State.Phase, Is.EqualTo(DeployPhase.Running));
        }

        [Test]
        public void AnImageOrSteamFleetIsRefusedWithTheSentenceThatSendsItOutsideThePlugin()
        {
            DeployRequest releaseOnly = Request(build: false, push: false);
            (List<DeployAction> image, DeployState _) = Drive(releaseOnly, Start(releaseOnly), Listed(Targets("image", null, null, TestVersion), T0));
            Assert.That(image.Last().Reason, Is.EqualTo(DeployFailure.DataSourceMismatch));
            Assert.That(image.Last().Message, Is.EqualTo("This fleet runs a container image. The plugin pushes to CDN sources only; push your image and release it outside the plugin."));

            (List<DeployAction> steam, DeployState _) = Drive(releaseOnly, Start(releaseOnly), Listed(Targets("steam_depot", null, null, TestVersion), T0));
            Assert.That(steam.Last().Message, Is.EqualTo(DataSourceRule.SteamMessage));
            Assert.That(DataSourceRule.Refusal("cdn_source"), Is.Null);
            Assert.That(DataSourceRule.Refusal(null), Does.Contain("not come from a CDN source"));
        }

        [Test]
        public void APushOfAFolderWithoutTheStartupExecutableFailsWithItsOwnReason()
        {
            DeployRequest request = Request(build: false);
            StepResult missing = StepResult.Failure(DeployActionKind.Push, new PluginError("push", PluginErrorKind.Refused, "Your game launches ./BeaconRush.x86_64; this build has no such file.", null) { Reason = DeployFailure.StartupExecutableMissing }, T0);
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request), missing);
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.StartupExecutableMissing), "[mutation: report every push refusal as push_failed]");
            Assert.That(actions.Last().Message, Is.EqualTo("Your game launches ./BeaconRush.x86_64; this build has no such file."));
            Assert.That(PipelineSteps.Derive(s, actions.Last(), PipelineStep.Push, false).Single(r => r.Step == PipelineStep.Push).Status, Is.EqualTo(PipelineStepStatus.Failed));
        }

        [Test]
        public void APushOnlyRunSendsTheChosenFolder()
        {
            DeployRequest request = Request(build: false, release: false);
            DeployState s = Start(request);
            Assert.That(s.BuildFolder, Is.EqualTo("Builds/Server/" + TestVersion));
            Assert.That(s.StartupExecutables, Is.EqualTo(new[] { Executable }));
            Assert.That(s.StartupAnyOf, Is.False);
            Assert.That(DeployPlanner.Next(request, s, null, T0).Action.Kind, Is.EqualTo(DeployActionKind.Push));
        }

        [Test]
        public void TheReleaseTargetsTheSnapshotPingctlPrintedNotTheBuildVersion()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request), Built(), Pushed("2026.10.06-p2fix", changed: false));
            Assert.That(s.Snapshot, Is.EqualTo("2026.10.06-p2fix"), "[mutation: Snapshot = BuildVersion]");
            Assert.That(s.SnapshotChanged, Is.False);
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.ReadBuildTargets));
        }

        [Test]
        public void APushThatPrintedNoSnapshotStopsBeforeAnyRelease()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request), Built(), Pushed(null, null));
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.Failed));
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.SnapshotUnknown), "[mutation: release the build version anyway]");
            Assert.That(s.Pushed, Is.False);
        }

        [Test]
        public void BuildTargetsArePolledEvery5SecondsForUpTo90SecondsOfPublishLag()
        {
            DeployRequest request = Request();
            BuildTargetsResponse lagging = Targets(versions: "2026.10.06-p2fix");
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request), Built(), Pushed(TestVersion),
                Listed(lagging, T0),
                Listed(lagging, T0.AddSeconds(45)),
                Listed(lagging, T0.AddSeconds(89)));
            Assert.That(actions.Skip(3).Select(a => (a.Kind, a.After)), Is.All.EqualTo((DeployActionKind.ReadBuildTargets, TimeSpan.FromSeconds(5))));
            Assert.That(s.TargetsChecks, Is.EqualTo(3));

            DeployPlan late = DeployPlanner.Next(request, DeployPlanner.Begin(s, actions.Last(), T0.AddSeconds(90)), Listed(lagging, T0.AddSeconds(90)), T0.AddSeconds(90));
            Assert.That(late.Action.Kind, Is.EqualTo(DeployActionKind.Failed), "[mutation: drop the lag limit, which polls forever]");
            Assert.That(late.Action.Reason, Is.EqualTo(DeployFailure.SnapshotNotListed));
            Assert.That(late.Action.Message, Does.Contain("2026.10.06-p2fix"), "the message names what the targets do list");
        }

        [Test]
        public void ALaggingSnapshotThatAppearsWithinTheWindowIsReleased()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState _) = Drive(request, Start(request), Built(), Pushed(TestVersion),
                Listed(Targets(versions: "old"), T0),
                Listed(Targets(versions: new[] { "old", TestVersion }), T0.AddSeconds(10)));
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.StartRelease));
        }

        [Test]
        public void ACdnBranchConfirmsTheSnapshotThroughCdnCurrentVersionAndAnImageBranchOnlyThroughItsTargets()
        {
            DeployRequest request = Request();
            (List<DeployAction> cdn, DeployState _) = Drive(request, Start(request), Built(), Pushed(TestVersion), Listed(Targets(cdnCurrent: TestVersion), T0));
            Assert.That(cdn.Last().Kind, Is.EqualTo(DeployActionKind.StartRelease));

            Assert.That(ReleaseWatcher.TargetsList(Targets("image", null, TestVersion), TestVersion), Is.False, "[mutation: accept cdnCurrentVersion for any data source]");
            Assert.That(ReleaseWatcher.TargetsList(Targets("image", null, null, TestVersion), TestVersion), Is.True);
            Assert.That(ReleaseWatcher.TargetsList(Targets(versions: "x"), null), Is.False);
        }

        [Test]
        public void ReleasingTheVersionTheFleetIsAlreadyPinnedToIsRefusedUnlessForced()
        {
            DeployRequest request = Request();
            DeployPlan plan = DeployPlanner.Next(request, Confirmed(request, pinned: TestVersion), null, T0);
            Assert.That(plan.Action.Kind, Is.EqualTo(DeployActionKind.Failed), "[mutation: drop the pinned check]");
            Assert.That(plan.Action.Reason, Is.EqualTo(DeployFailure.AlreadyPinned));

            DeployRequest forced = Request(force: true);
            Assert.That(DeployPlanner.Next(forced, Confirmed(forced, pinned: TestVersion), null, T0).Action.Kind, Is.EqualTo(DeployActionKind.StartRelease));
            Assert.That(DeployPlanner.Next(request, Confirmed(request), null, T0).Action.Kind, Is.EqualTo(DeployActionKind.StartRelease), "another pin releases");
        }

        [TestCase(DeployFailure.ReleaseInProgress, 409, DeployFailure.ReleaseInProgress)]
        [TestCase(null, 409, DeployFailure.ReleaseInProgress)]
        [TestCase(DeployFailure.SnapshotNotFound, 404, DeployFailure.SnapshotNotFound)]
        [TestCase(DeployFailure.CdnUnreadable, 502, DeployFailure.CdnUnreadable)]
        [TestCase(DeployFailure.TooManyPins, 409, DeployFailure.TooManyPins)]
        [TestCase(DeployFailure.SupervisorTooOld, 409, DeployFailure.SupervisorTooOld)]
        [TestCase("something_new", 400, DeployFailure.ReleaseRefused)]
        public void EveryReleaseRefusalStopsWithItsReasonAndIsNeverRetried(string reason, int status, string expected)
        {
            DeployRequest request = Request();
            DeployState s = Confirmed(request);
            DeployAction start = DeployPlanner.Next(request, s, null, T0).Action;
            PluginErrorKind kind = status == 409 ? PluginErrorKind.Conflict : status == 404 ? PluginErrorKind.NotFound : PluginErrorKind.Rejected;
            DeployPlan refused = DeployPlanner.Next(request, DeployPlanner.Begin(s, start, T0), Fail(DeployActionKind.StartRelease, kind, reason, status), T0);
            Assert.That(refused.Action.Kind, Is.EqualTo(DeployActionKind.Failed));
            Assert.That(refused.Action.Reason, Is.EqualTo(expected));
            Assert.That(refused.Action.Message, Is.Not.Empty);
            Assert.That(DeployPlanner.Next(request, refused.State, null, T0).Action.Kind, Is.EqualTo(DeployActionKind.Failed), "a failed run never starts a release again");
        }

        [Test]
        public void AnUnreachableGameServerIsRetriedOnceAfter30SecondsThenStops()
        {
            DeployRequest request = Request();
            DeployState s = Confirmed(request);
            DeployAction start = DeployPlanner.Next(request, s, null, T0).Action;
            DeployPlan first = DeployPlanner.Next(request, DeployPlanner.Begin(s, start, T0), Fail(DeployActionKind.StartRelease, PluginErrorKind.Rejected, DeployFailure.ServerUnreachable, 409), T0);
            Assert.That(first.Action.Kind, Is.EqualTo(DeployActionKind.StartRelease));
            Assert.That(first.Action.After, Is.EqualTo(TimeSpan.FromSeconds(30)));

            DeployPlan second = DeployPlanner.Next(request, DeployPlanner.Begin(first.State, first.Action, T0), Fail(DeployActionKind.StartRelease, PluginErrorKind.Rejected, DeployFailure.ServerUnreachable, 409), T0);
            Assert.That(second.Action.Kind, Is.EqualTo(DeployActionKind.Failed), "[mutation: retry on every attempt]");
            Assert.That(second.Action.Reason, Is.EqualTo(DeployFailure.ServerUnreachable));
        }

        [TestCase(PluginErrorKind.Transport)]
        [TestCase(PluginErrorKind.Envelope)]
        public void AReleaseRequestWithNoUsableAnswerIsReportedNotResent(PluginErrorKind kind)
        {
            DeployRequest request = Request();
            DeployState s = Confirmed(request);
            DeployAction start = DeployPlanner.Next(request, s, null, T0).Action;
            DeployPlan plan = DeployPlanner.Next(request, DeployPlanner.Begin(s, start, T0), Fail(DeployActionKind.StartRelease, kind), T0);
            Assert.That(plan.Action.Kind, Is.EqualTo(DeployActionKind.Failed));
            Assert.That(plan.Action.Reason, Is.EqualTo(DeployFailure.ReleaseOutcomeUnknown));
        }

        [Test]
        public void AFailedReleaseIsAcknowledgedAndThePipelineStopsThere()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState s) = Drive(request, Confirmed(request),
                Created(12),
                Polled(Detail(12, "failed", null, "surge_timeout")),
                Ok(DeployActionKind.Acknowledge, T0));
            Assert.That(actions.Select(a => a.Kind).Skip(1), Is.EqualTo(new[] { DeployActionKind.PollRelease, DeployActionKind.Acknowledge, DeployActionKind.Failed }));
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.ReleaseFailed));
            Assert.That(actions.Last().Message, Does.Contain("surge_timeout").And.Contain("acknowledged"));
            Assert.That(s.Acknowledged, Is.True);
            Assert.That(DeployPlanner.Next(request, s, null, T0).Action.Kind, Is.EqualTo(DeployActionKind.Failed), "[mutation: retry the release after acknowledging]");
        }

        [Test]
        public void AnAcknowledgementThatFailsStillStopsAndSaysTheReleaseHoldsScaleDown()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState s) = Drive(request, Confirmed(request),
                Created(12), Polled(Detail(12, "failed")), Fail(DeployActionKind.Acknowledge, PluginErrorKind.Transport));
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.Failed));
            Assert.That(actions.Last().Message, Does.Contain("holds scale-down"));
            Assert.That(s.Acknowledged, Is.False);
        }

        [Test]
        public void AnAlreadyAcknowledgedFailureIsNotAcknowledgedAgain()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState _) = Drive(request, Confirmed(request), Created(12), Polled(Detail(12, "failed", "2026-10-07 10:01:00")));
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.Failed));
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.ReleaseFailed));
        }

        [TestCase("rolled_back")]
        [TestCase("dismissed")]
        public void ARolledBackOrCancelledReleaseEndsTheWatch(string state)
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState _) = Drive(request, Confirmed(request), Created(12), Polled(Detail(12, state)));
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.Failed));
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.ReleaseEnded));
        }

        [Test]
        public void AfterADomainReloadTheSameReleaseIsWatchedAgainNotStartedAgain()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState s) = Drive(request, Confirmed(request), Created(12));
            DeployState saved = DeployPlanner.Begin(s, actions.Last(), T0);
            Assert.That(saved.InFlight, Is.EqualTo(DeployActionKind.PollRelease));

            DeployPlan resumed = DeployPlanner.Next(request, saved, null, T0.AddMinutes(3));
            Assert.That(resumed.Action.Kind, Is.EqualTo(DeployActionKind.PollRelease), "[mutation: decide from scratch, which starts a second release]");
            Assert.That(resumed.Action.After, Is.EqualTo(TimeSpan.Zero));
            Assert.That(resumed.State.ReleaseId, Is.EqualTo(12));
            Assert.That(resumed.State.InFlight, Is.Null);
        }

        [Test]
        public void AReleaseRequestInterruptedByAReloadIsReportedNeverResent()
        {
            DeployRequest request = Request();
            DeployState s = Confirmed(request);
            DeployState saved = DeployPlanner.Begin(s, DeployPlanner.Next(request, s, null, T0).Action, T0);
            Assert.That(saved.InFlight, Is.EqualTo(DeployActionKind.StartRelease));
            DeployPlan resumed = DeployPlanner.Next(request, saved, null, T0);
            Assert.That(resumed.Action.Kind, Is.EqualTo(DeployActionKind.Failed), "[mutation: treat it like any other interrupted step]");
            Assert.That(resumed.Action.Reason, Is.EqualTo(DeployFailure.ReleaseOutcomeUnknown));
        }

        [Test]
        public void AnInterruptedPushRunsAgainAfterAReload()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request), Built());
            DeployPlan resumed = DeployPlanner.Next(request, DeployPlanner.Begin(s, actions.Last(), T0), null, T0);
            Assert.That(resumed.Action.Kind, Is.EqualTo(DeployActionKind.Push));
            Assert.That(resumed.Notes.Single(), Does.Contain("Push"));
        }

        [Test]
        public void PollFailuresAreRetriedFiveTimesThenTheWatchStopsWithTheReleaseLeftRunning()
        {
            DeployRequest request = Request();
            StepResult down = Fail(DeployActionKind.PollRelease, PluginErrorKind.Transport);
            (List<DeployAction> actions, DeployState s) = Drive(request, Confirmed(request), Created(12), down, down, down, down, down, down);
            Assert.That(actions.Skip(2).Take(5).Select(a => a.Kind), Is.All.EqualTo(DeployActionKind.PollRelease));
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.Failed));
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.PollFailed));
            Assert.That(s.ReleaseUnfinished, Is.True, "the release may still be moving, so the run stays resumable");

            (List<DeployAction> recovered, DeployState r) = Drive(request, Confirmed(request), Created(12), down, Polled(Detail(12, "rolling")));
            Assert.That(r.PollFailures, Is.Zero, "a good poll resets the count");
        }

        [Test]
        public void ARateLimitedPollWaitsWhatTheWorkspaceAsked()
        {
            DeployRequest request = Request();
            StepResult limited = StepResult.Failure(DeployActionKind.PollRelease, new PluginError("release", PluginErrorKind.RateLimited, "slow down", null) { RetryAfter = TimeSpan.FromSeconds(20) }, T0);
            (List<DeployAction> actions, DeployState _) = Drive(request, Confirmed(request), Created(12), limited);
            Assert.That(actions.Last().Kind, Is.EqualTo(DeployActionKind.PollRelease));
            Assert.That(actions.Last().After, Is.EqualTo(TimeSpan.FromSeconds(20)));
        }

        [Test]
        public void AFleetOnAnotherDataSourceIsRefusedBeforeRelease()
        {
            DeployRequest request = Request();
            (List<DeployAction> actions, DeployState _) = Drive(request, Start(request), Built(), Pushed(TestVersion), Listed(Targets("image", null, null, TestVersion), T0));
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.DataSourceMismatch), "[mutation: skip the data source check]");

            DeployRequest releaseOnly = Request(build: false, push: false);
            (List<DeployAction> depot, DeployState _) = Drive(releaseOnly, Start(releaseOnly), Listed(Targets("steam_depot", null, null, TestVersion), T0));
            Assert.That(depot.Last().Reason, Is.EqualTo(DeployFailure.DataSourceMismatch));
        }

        [Test]
        public void AReleaseOnlyRunReleasesTheVersionItNames()
        {
            DeployRequest request = Request(build: false, push: false);
            (List<DeployAction> actions, DeployState s) = Drive(request, Start(request), Listed(Targets(versions: TestVersion), T0));
            Assert.That(actions.Select(a => a.Kind), Is.EqualTo(new[] { DeployActionKind.ReadBuildTargets, DeployActionKind.StartRelease }));
            Assert.That(s.Snapshot, Is.EqualTo(TestVersion));
        }

        [Test]
        public void InvalidRequestsAreRefusedBeforeAnyStep()
        {
            DeployRequest none = Request(false, false, false);
            Assert.That(DeployPlanner.Next(none, Start(none), null, T0).Action.Reason, Is.EqualTo(DeployFailure.InvalidRequest));

            DeployRequest noFleet = Request();
            noFleet.FleetId = 0;
            Assert.That(DeployPlanner.Next(noFleet, Start(noFleet), null, T0).Action.Reason, Is.EqualTo(DeployFailure.InvalidRequest));

            DeployRequest badVersion = Request();
            badVersion.BuildVersion = "../escape";
            Assert.That(DeployPlanner.Next(badVersion, Start(badVersion), null, T0).Action.Reason, Is.EqualTo(DeployFailure.InvalidRequest));

            DeployRequest noSource = Request();
            noSource.CdnSourceId = 0;
            Assert.That(DeployPlanner.Next(noSource, Start(noSource), null, T0).Action.Message, Is.EqualTo("The branch names no CDN source, so there is nothing to push to."), "a push never guesses a source");

            DeployRequest noProfile = Request();
            noProfile.BuildProfile = null;
            Assert.That(DeployPlanner.Next(noProfile, Start(noProfile), null, T0).Action.Reason, Is.EqualTo(DeployFailure.InvalidRequest));

            DeployRequest noFolder = Request(build: false);
            noFolder.PushFolder = " ";
            Assert.That(DeployPlanner.Next(noFolder, Start(noFolder), null, T0).Action.Message, Does.Contain("Choose the folder to push"));

            DeployRequest noStartup = Request(build: false);
            noStartup.Startup = null;
            Assert.That(DeployPlanner.Next(noStartup, Start(noStartup), null, T0).Action.Message, Does.Contain("startup command is not known"));
            noStartup.Startup = new StartupFiles(new string[0], false);
            Assert.That(noStartup.Problem(), Does.Contain("startup command is not known"), "an empty file list is no startup command");
            noStartup.Startup = null;

            // A check skipped on purpose (no command-line config names a process) is a reason, not a refusal.
            noStartup.StartupCheckSkipped = StartupCheck.SkippedPrefix + "no deployment of the fleet names a process to launch.";
            Assert.That(noStartup.Problem(), Is.Null);
            Assert.That(DeployPlanner.Next(noStartup, Start(noStartup), null, T0).Action.Kind, Is.EqualTo(DeployActionKind.Push));
        }

        [Test]
        public void AFailedBuildStopsAndAStoppedStepReadsCancelled()
        {
            DeployRequest request = Request();
            (List<DeployAction> failed, DeployState s) = Drive(request, Start(request), Fail(DeployActionKind.Build, PluginErrorKind.ChildFailed));
            Assert.That(failed.Last().Reason, Is.EqualTo(DeployFailure.BuildFailed));
            Assert.That(s.Built, Is.False);

            (List<DeployAction> stopped, DeployState _) = Drive(request, Start(request), Fail(DeployActionKind.Build, PluginErrorKind.Cancelled));
            Assert.That(stopped.Last().Reason, Is.EqualTo(DeployFailure.Cancelled));
        }

        [Test]
        public void AMissingBuildFolderIsReportedAsNoBuild()
        {
            DeployRequest request = Request(build: false);
            StepResult noBuild = StepResult.Failure(DeployActionKind.Push, new PluginError("push", PluginErrorKind.Refused, "There is no build", null) { Reason = DeployFailure.NoBuild }, T0);
            (List<DeployAction> actions, DeployState _) = Drive(request, Start(request), noBuild);
            Assert.That(actions.Last().Reason, Is.EqualTo(DeployFailure.NoBuild));
        }

        [Test]
        public void ThePlannerNeverMutatesTheStateItWasGiven()
        {
            DeployRequest request = Request();
            DeployState s = Start(request);
            string before = Newtonsoft.Json.JsonConvert.SerializeObject(s);
            DeployPlanner.Next(request, s, Built(), T0.AddMinutes(1));
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(s), Is.EqualTo(before));
        }
    }
}
