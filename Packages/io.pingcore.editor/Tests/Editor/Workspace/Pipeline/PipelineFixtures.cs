using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>Values the pipeline tests share: requests, answers and the planner driven step by step.</summary>
    internal static class PipelineFixtures
    {
        public static readonly DateTime T0 = new DateTime(2026, 10, 7, 10, 0, 0, DateTimeKind.Utc);

        public const string TestVersion = "2026.10.07-editor1";

        /// <summary>A push token shaped like the real thing, built so no token literal sits in source.</summary>
        public static string FakePushToken() => "cdnpush" + "_" + "Tq7" + new string('x', 20) + "Zk9";

        /// <summary>The build profile the fixtures build with.</summary>
        public const string Profile = "Assets/Settings/Build Profiles/Linux Server.asset";

        /// <summary>The executable the fixtures' startup command launches.</summary>
        public const string Executable = "BeaconRush.x86_64";

        public static DeployRequest Request(bool build = true, bool push = true, bool release = true, bool tokenStored = true, bool force = false)
        {
            return new DeployRequest
            {
                Build = build,
                Push = push,
                Release = release,
                BuildVersion = TestVersion,
                FleetId = 1,
                CdnSourceId = 31,
                ForceRelease = force,
                PushTokenStored = tokenStored,
                BuildProfile = Profile,
                BuildExecutable = Executable,
                Startup = StartupFiles.One(Executable),
                PushFolder = build ? null : "Builds/Server/" + TestVersion,
            };
        }

        public static DeployState Start(DeployRequest request) => DeployState.Start(request, T0, "run1");

        public static BuildTargetsResponse Targets(string dataSource = "cdn_source", string pinned = "2026.10.06-p2fix", string cdnCurrent = null, params string[] versions)
        {
            return new BuildTargetsResponse
            {
                DataSourceType = dataSource,
                PinnedBuildVersion = pinned,
                CdnCurrentVersion = cdnCurrent,
                Targets = versions.Select(v => new BuildTargetView { BuildVersion = v, Label = v }).ToList(),
            };
        }

        public static ReleaseDetailResponse Detail(long releaseId, string state, string acknowledgedAt = null, string failedReason = null, params ReleaseDeploymentView[] deployments)
        {
            return new ReleaseDetailResponse
            {
                Release = new ReleaseStatusView { ReleaseId = releaseId, FleetId = 1, State = state, AcknowledgedAt = acknowledgedAt, FailedReason = failedReason, TargetBuildVersion = TestVersion },
                Deployments = deployments.ToList(),
            };
        }

        public static ReleaseDeploymentView Location(long id, string phase, int oldCount, int newCount, int retiring = 0, string blocked = null)
        {
            return new ReleaseDeploymentView { BrandDeploymentId = id, Phase = phase, OldCount = oldCount, NewCount = newCount, RetiringCount = retiring, Blocked = blocked };
        }

        public static StepResult Ok(DeployActionKind kind, DateTime at) => StepResult.Success(kind, at);

        public static StepResult Fail(DeployActionKind kind, PluginErrorKind errorKind, string reason = null, int status = 0, DateTime at = default)
        {
            return StepResult.Failure(kind, new PluginError("test", errorKind, "refused by the test", null) { Reason = reason, HttpStatus = status }, at == default ? T0 : at);
        }

        /// <summary>Drives the planner from <paramref name="state"/> through <paramref name="results"/>, recording every action.</summary>
        public static (List<DeployAction> Actions, DeployState State) Drive(DeployRequest request, DeployState state, params StepResult[] results)
        {
            var actions = new List<DeployAction>();
            DeployPlan plan = DeployPlanner.Next(request, state, null, T0);
            actions.Add(plan.Action);
            DeployState s = plan.State;
            foreach (StepResult r in results)
            {
                s = DeployPlanner.Begin(s, plan.Action, r.AtUtc == default ? T0 : r.AtUtc);
                plan = DeployPlanner.Next(request, s, r, r.AtUtc == default ? T0 : r.AtUtc);
                actions.Add(plan.Action);
                s = plan.State;
            }

            return (actions, s);
        }
    }
}
