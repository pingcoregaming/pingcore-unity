using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>Where a pipeline run stands as a whole.</summary>
    public enum DeployPhase
    {
        /// <summary>Planned or running.</summary>
        Running,

        /// <summary>Stopped until the developer acts (a push token).</summary>
        NeedsInput,

        Done,

        Failed,
    }

    /// <summary>
    /// The in-flight pipeline, persisted to <c>UserSettings/PingCoreDeployState.json</c> after
    /// every step so a domain reload or an Editor restart can resume it ("release 12 still
    /// running, resume watching?"). An earlier version's <c>pushPath</c>, <c>imageReference</c> and
    /// <c>continueAfterReload</c> are ignored when read. Ids, versions and states only: never a credential (saving
    /// refuses a credential-shaped value), never a response body.
    /// </summary>
    public sealed class DeployState
    {
        /// <summary>The file's format marker.</summary>
        public const string Format = "pingcore-deploy-state/1";

        [JsonProperty("format", Order = 0)]
        public string FileFormat { get; set; } = Format;

        /// <summary>A random id of this run, so a resumed run is recognisably the same one.</summary>
        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("startedUtc")]
        public DateTime StartedUtc { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }

        [JsonProperty("phase")]
        [JsonConverter(typeof(StringEnumConverter))]
        public DeployPhase Phase { get; set; } = DeployPhase.Running;

        // The request, frozen when the run started.
        [JsonProperty("wantBuild")]
        public bool WantBuild { get; set; }

        [JsonProperty("wantPush")]
        public bool WantPush { get; set; }

        [JsonProperty("wantRelease")]
        public bool WantRelease { get; set; }

        [JsonProperty("forceRelease")]
        public bool ForceRelease { get; set; }

        [JsonProperty("buildVersion")]
        public string BuildVersion { get; set; }

        [JsonProperty("fleetId")]
        public long FleetId { get; set; }

        [JsonProperty("cdnSourceId")]
        public long CdnSourceId { get; set; }

        /// <summary>The action whose result has not been recorded yet: set before a step starts, cleared when its result is folded in. Seen on resume, it means the Editor stopped mid-step.</summary>
        [JsonProperty("inFlight", NullValueHandling = NullValueHandling.Include)]
        [JsonConverter(typeof(StringEnumConverter))]
        public DeployActionKind? InFlight { get; set; }

        // Build.
        [JsonProperty("built")]
        public bool Built { get; set; }

        /// <summary>The folder the push sends: project-relative after a build (<c>Builds/Server/2026.10.07-editor1</c>), or the folder the developer chose (absolute).</summary>
        [JsonProperty("buildFolder", NullValueHandling = NullValueHandling.Include)]
        public string BuildFolder { get; set; }

        // Push.

        /// <summary>The files the game's startup command launches, paths inside the build; the push checks the folder holds them (<see cref="DeployRequest.Startup"/>).</summary>
        [JsonProperty("startupExecutables", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> StartupExecutables { get; set; }

        /// <summary>True when the build needs only one of <see cref="StartupExecutables"/> (the game's template sets, no deployment).</summary>
        [JsonProperty("startupAnyOf")]
        public bool StartupAnyOf { get; set; }

        /// <summary>An earlier version's single <c>startupExecutable</c>: read into <see cref="StartupExecutables"/>, never written.</summary>
        [JsonProperty("startupExecutable")]
        private string LegacyStartupExecutable
        {
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && (StartupExecutables == null || StartupExecutables.Count == 0))
                {
                    StartupExecutables = new List<string> { value };
                }
            }
        }

        /// <summary>The saved startup files, or null.</summary>
        [JsonIgnore]
        public StartupFiles Startup
        {
            get => StartupExecutables == null || StartupExecutables.Count == 0 ? null : new StartupFiles(StartupExecutables, StartupAnyOf);
            set
            {
                StartupExecutables = value == null || value.Paths.Count == 0 ? null : new List<string>(value.Paths);
                StartupAnyOf = value != null && value.AnyOf;
            }
        }

        /// <summary>Why the startup command check was skipped for this push, or null (<see cref="DeployRequest.StartupCheckSkipped"/>).</summary>
        [JsonProperty("startupCheckSkipped", NullValueHandling = NullValueHandling.Ignore)]
        public string StartupCheckSkipped { get; set; }

        [JsonProperty("pushed")]
        public bool Pushed { get; set; }

        /// <summary>The version the release targets: the snapshot pingctl printed, or the version a release-only run names.</summary>
        [JsonProperty("snapshot", NullValueHandling = NullValueHandling.Include)]
        public string Snapshot { get; set; }

        [JsonProperty("snapshotChanged", NullValueHandling = NullValueHandling.Include)]
        public bool? SnapshotChanged { get; set; }

        // Build targets.
        [JsonProperty("targetsFirstCheckedUtc", NullValueHandling = NullValueHandling.Include)]
        public DateTime? TargetsFirstCheckedUtc { get; set; }

        [JsonProperty("targetsChecks")]
        public int TargetsChecks { get; set; }

        [JsonProperty("targetsConfirmed")]
        public bool TargetsConfirmed { get; set; }

        [JsonProperty("dataSourceType", NullValueHandling = NullValueHandling.Include)]
        public string DataSourceType { get; set; }

        [JsonProperty("pinnedVersion", NullValueHandling = NullValueHandling.Include)]
        public string PinnedVersion { get; set; }

        // Release.
        [JsonProperty("releaseAttempts")]
        public int ReleaseAttempts { get; set; }

        [JsonProperty("releaseId")]
        public long ReleaseId { get; set; }

        [JsonProperty("releaseState", NullValueHandling = NullValueHandling.Include)]
        public string ReleaseState { get; set; }

        [JsonProperty("releaseBlocked", NullValueHandling = NullValueHandling.Include)]
        public string ReleaseBlocked { get; set; }

        [JsonProperty("releaseFailedReason", NullValueHandling = NullValueHandling.Include)]
        public string ReleaseFailedReason { get; set; }

        [JsonProperty("locations")]
        public List<ReleaseLocationProgress> Locations { get; set; } = new List<ReleaseLocationProgress>();

        [JsonProperty("pollFailures")]
        public int PollFailures { get; set; }

        [JsonProperty("acknowledged")]
        public bool Acknowledged { get; set; }

        // Outcome.
        [JsonProperty("failure", NullValueHandling = NullValueHandling.Include)]
        public string Failure { get; set; }

        [JsonProperty("failureMessage", NullValueHandling = NullValueHandling.Include)]
        public string FailureMessage { get; set; }

        /// <summary>A fresh state for <paramref name="request"/>.</summary>
        public static DeployState Start(DeployRequest request, DateTime nowUtc, string runId)
        {
            return new DeployState
            {
                RunId = runId,
                StartedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                WantBuild = request.Build,
                WantPush = request.Push,
                WantRelease = request.Release,
                ForceRelease = request.ForceRelease,
                BuildVersion = request.BuildVersion,
                FleetId = request.FleetId,
                CdnSourceId = request.CdnSourceId,
                BuildFolder = request.Build ? null : request.PushFolder,
                Startup = request.Startup,
                StartupCheckSkipped = request.StartupCheckSkipped,
            };
        }

        /// <summary>True while a release this run started may still be moving: it has an id and no final state.</summary>
        [JsonIgnore]
        public bool ReleaseUnfinished => ReleaseId > 0 && !ReleaseWatcher.IsFinal(ReleaseState) && Phase != DeployPhase.Done;

        /// <summary>A deep copy (the planner never mutates its input).</summary>
        public DeployState Clone()
        {
            return JObject.FromObject(this, JsonSerializer.Create(DeployStateFile.Json)).ToObject<DeployState>(JsonSerializer.Create(DeployStateFile.Json));
        }
    }
}
