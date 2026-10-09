using System.Text.RegularExpressions;
using PingCore.Editor.Build;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// What a Ship row asked the pipeline for: one of build, push and release, onto which fleet and CDN
    /// source, at which build version, from which build profile or folder. Ids, asset paths and folders only.
    /// The plugin pushes to CDN sources only, through the bundled <c>pingctl</c>.
    /// </summary>
    public sealed class DeployRequest
    {
        private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        /// <summary>Build the Linux Dedicated Server through <see cref="BuildProfile"/>.</summary>
        public bool Build { get; set; }

        /// <summary>Push <see cref="PushFolder"/> (or the build this run made) to the fleet's CDN source.</summary>
        public bool Push { get; set; }

        /// <summary>Release onto the fleet and watch the rollout.</summary>
        public bool Release { get; set; }

        /// <summary>The build version (the build's folder and <c>version.txt</c>); for a release-only run, the version to release.</summary>
        public string BuildVersion { get; set; }

        public long FleetId { get; set; }

        /// <summary>The CDN source Push uploads to: the picked branch's (<c>GET my-games/{id}</c>); 0 when it names none.</summary>
        public long CdnSourceId { get; set; }

        /// <summary>The Linux Dedicated Server build profile (<c>Assets/...asset</c>).</summary>
        public string BuildProfile { get; set; }

        /// <summary>The executable the build writes, a path inside the build (<c>BeaconRush.x86_64</c>).</summary>
        public string BuildExecutable { get; set; }

        /// <summary>The folder a push-only run sends: absolute, or relative to the project.</summary>
        public string PushFolder { get; set; }

        /// <summary>
        /// The files the game's startup command launches (paths inside the build): every one for a fleet with deployments,
        /// any one for a fleet without (<see cref="StartupCheck.Files"/>); the push refuses a build that does not hold them.
        /// </summary>
        public StartupFiles Startup { get; set; }

        /// <summary>
        /// Why the startup command check was skipped (no deployment's or template set's command-line config names a
        /// process), or null. Only with a reason does a push run without <see cref="Startup"/>, and then the folder check
        /// skips the file check alone: the build guard's verdict is still required.
        /// </summary>
        public string StartupCheckSkipped { get; set; }

        /// <summary>Release even when the fleet is already pinned to this version.</summary>
        public bool ForceRelease { get; set; }

        /// <summary>Filled by the runner before each planning call: a push token is stored for the source.</summary>
        public bool PushTokenStored { get; set; }

        /// <summary>The product <c>pingctl</c> names Unity's do-not-ship folders after: the executable's file name without its extension.</summary>
        public string ExcludeProduct
        {
            get
            {
                // The first startup file even when any one is enough: pingctl also leaves out any top-level folder with
                // either do-not-ship suffix, whatever the product, so the choice only names the exact folder.
                string exe = (HasStartup ? Startup.Paths[0] : null) ?? BuildExecutable ?? string.Empty;
                string name = exe.Substring(exe.LastIndexOf('/') + 1);
                return System.IO.Path.GetFileNameWithoutExtension(name);
            }
        }

        /// <summary>True when <see cref="Startup"/> names at least one file.</summary>
        public bool HasStartup => Startup != null && Startup.Paths.Count > 0;

        /// <summary>Why this request cannot run, or null. Pure.</summary>
        public string Problem()
        {
            if (!Build && !Push && !Release)
            {
                return "Choose one of build, push and release.";
            }

            if (BuildVersion == null || !NamePattern.IsMatch(BuildVersion))
            {
                return "The build version must be 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit.";
            }

            if (Build)
            {
                if (string.IsNullOrEmpty(BuildProfile))
                {
                    return "Pick the Linux Dedicated Server build profile to build with.";
                }

                string profileProblem = Cli.BuildServerArgs.CheckBuildProfilePath(BuildProfile);
                if (profileProblem != null)
                {
                    return profileProblem;
                }

                string exeProblem = ServerBuildOptions.ExecutableProblem(BuildExecutable);
                if (exeProblem != null)
                {
                    return "The build cannot be written: " + exeProblem + ".";
                }
            }

            if (Release && FleetId <= 0)
            {
                return "Pick the fleet to release onto under Connect.";
            }

            if (Push)
            {
                if (CdnSourceId <= 0)
                {
                    return "The branch names no CDN source, so there is nothing to push to.";
                }

                if (!Build && string.IsNullOrWhiteSpace(PushFolder))
                {
                    return "Choose the folder to push, or build first.";
                }

                if (!HasStartup && string.IsNullOrWhiteSpace(StartupCheckSkipped))
                {
                    return "The game's startup command is not known, so the build cannot be checked against it. " + StartupCommand.FixInPanel;
                }
            }

            return null;
        }
    }
}
