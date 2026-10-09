using System;
using System.IO;
using Newtonsoft.Json;

namespace PingCore.Editor.Workspace.Settings
{
    /// <summary>
    /// The plugin's shared project settings, <c>ProjectSettings/PingCoreEditor.json</c>: ids (the
    /// game, the fleet and the branch Push pushes to), asset paths and the picked process name only, committed with the project and shared with the team. Never a credential:
    /// <see cref="Save"/> refuses any value that looks like one. There is no workspace URL: the
    /// API resolves the workspace from the key. Fields an earlier version wrote (<c>workspaceHost</c>,
    /// <c>cdnSourceId</c>, <c>productName</c>, <c>scenes</c>, <c>supervisorBaseTag</c>, <c>pushPath</c>)
    /// are ignored when read and dropped on the next save: the CDN source comes from the fleet's
    /// build targets, and the scenes and backend from the build profile.
    /// </summary>
    public sealed class EditorProjectSettings
    {
        /// <summary>The file's format marker.</summary>
        public const string Format = "pingcore-editor-settings/1";

        /// <summary>The path under the project root.</summary>
        public const string RelativePath = "ProjectSettings/PingCoreEditor.json";

        [JsonProperty("format", Order = 0)]
        public string FileFormat { get; set; } = Format;

        /// <summary>The game of the fleet Connect picked, or 0.</summary>
        [JsonProperty("gameId", Order = 1)]
        public long GameId { get; set; }

        /// <summary>The fleet Connect picked (Ship releases onto it), or 0.</summary>
        [JsonProperty("fleetId", Order = 2)]
        public long FleetId { get; set; }

        /// <summary>
        /// The game branch Ship's Push pushes to (Push to branch), or 0: the game's only branch, or none picked yet. An id,
        /// never a secret; cleared when Connect picks a fleet of another game.
        /// </summary>
        [JsonProperty("gameBranchId", Order = 3)]
        public long GameBranchId { get; set; }

        /// <summary>The Linux Dedicated Server build profile Ship builds with (<c>Assets/...asset</c>), or null.</summary>
        [JsonProperty("buildProfile", NullValueHandling = NullValueHandling.Include, Order = 4)]
        public string BuildProfile { get; set; }

        /// <summary>
        /// The process name Ship's Build names the executable after when the game launches several files (Server executable),
        /// as the panel writes it (<c>./BeaconRushServer.x86_64</c>), or null: the game launches one file, or none is picked.
        /// A name, never a secret; cleared when Connect picks a fleet of another game.
        /// </summary>
        [JsonProperty("processName", NullValueHandling = NullValueHandling.Ignore, Order = 5)]
        public string ProcessName { get; set; }

        /// <summary>The settings file of a project.</summary>
        public static string PathIn(string projectRoot) => System.IO.Path.Combine(projectRoot, "ProjectSettings", "PingCoreEditor.json");

        /// <summary>Loads the project's settings; defaults when the file is absent. Throws <see cref="InvalidDataException"/> for a malformed file.</summary>
        public static EditorProjectSettings Load(string projectRoot)
        {
            return SettingsFile.Load<EditorProjectSettings>(PathIn(projectRoot), Format);
        }

        /// <summary>Writes the settings (UTF-8, no BOM, LF, indented). Refuses a value that looks like a credential.</summary>
        public void Save(string projectRoot)
        {
            Validate();
            SettingsFile.Save(PathIn(projectRoot), this);
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> naming the first field that is not an id or an asset path.</summary>
        public void Validate()
        {
            SettingsFile.RefuseSecrets(this);
            if (GameId < 0 || FleetId < 0 || GameBranchId < 0)
            {
                throw new InvalidOperationException("Ids are 0 (unset) or positive.");
            }

            if (BuildProfile != null && (!BuildProfile.StartsWith("Assets/", StringComparison.Ordinal) || !BuildProfile.EndsWith(".asset", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("buildProfile must be a project path Assets/...asset.");
            }

            if (ProcessName != null && (ProcessName.Length > 256 || ProcessName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0))
            {
                throw new InvalidOperationException("processName must be one line of at most 256 characters.");
            }
        }
    }

    /// <summary>
    /// Per-developer settings, <c>UserSettings/PingCoreEditorUser.json</c>: a developer's own
    /// <c>pingctl</c>, the folder they chose to push, the session-only choice and the development
    /// API override; never committed and never a credential. Fields an earlier version wrote
    /// (<c>dockerPath</c>, <c>registryCredentialIds</c>) are ignored and dropped on the next save.
    /// </summary>
    public sealed class EditorUserSettings
    {
        /// <summary>The file's format marker.</summary>
        public const string Format = "pingcore-editor-user/1";

        /// <summary>The path under the project root.</summary>
        public const string RelativePath = "UserSettings/PingCoreEditorUser.json";

        [JsonProperty("format", Order = 0)]
        public string FileFormat { get; set; } = Format;

        /// <summary>
        /// The developer's own <c>pingctl</c> executable, used instead of the one the package carries; null for the
        /// bundled binary (<c>Pipeline/PingctlLocator.cs</c>).
        /// </summary>
        [JsonProperty("pingctlPath", NullValueHandling = NullValueHandling.Include, Order = 1)]
        public string PingctlPath { get; set; }

        /// <summary>The folder Push sends (the last build's, or one the developer chose), or null.</summary>
        [JsonProperty("pushFolder", NullValueHandling = NullValueHandling.Include, Order = 2)]
        public string PushFolder { get; set; }

        /// <summary>True to keep the key for this Editor session only (<c>SessionState</c>), never on disk.</summary>
        [JsonProperty("sessionOnlyKey", Order = 3)]
        public bool SessionOnlyKey { get; set; }

        /// <summary>
        /// A development-only API base (<c>https://host</c> or <c>https://host/api</c>) used instead of
        /// <see cref="Api.WorkspaceEndpoint.DefaultApiBase"/>; null for the default. Set by hand in this
        /// file, never in the UI. https only: <see cref="Save"/> refuses anything else, and a file edited
        /// by hand to something else makes every call refuse (<see cref="Api.WorkspaceEndpoint.Resolve"/>).
        /// </summary>
        [JsonProperty("apiBaseOverride", NullValueHandling = NullValueHandling.Ignore, Order = 4)]
        public string ApiBaseOverride { get; set; }

        /// <summary>The settings file of a project.</summary>
        public static string PathIn(string projectRoot) => System.IO.Path.Combine(projectRoot, "UserSettings", "PingCoreEditorUser.json");

        /// <summary>Loads; defaults when absent. Throws <see cref="InvalidDataException"/> for a malformed file.</summary>
        public static EditorUserSettings Load(string projectRoot)
        {
            return SettingsFile.Load<EditorUserSettings>(PathIn(projectRoot), Format);
        }

        /// <summary>Writes the settings. Refuses a value that looks like a credential and an API base override that is not https.</summary>
        public void Save(string projectRoot)
        {
            SettingsFile.RefuseSecrets(this);
            if (!Api.WorkspaceEndpoint.Resolve(ApiBaseOverride, out _, out string problem))
            {
                throw new InvalidOperationException(problem);
            }

            SettingsFile.Save(PathIn(projectRoot), this);
        }
    }
}
