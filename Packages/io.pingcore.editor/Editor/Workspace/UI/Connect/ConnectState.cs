using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Discovery.Client;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using UnityEditor;

namespace PingCore.Editor.Workspace.UI.Connect
{
    /// <summary>
    /// What Connect learned in this window, shared with Ship and Status: the workspace's fleets and the picked
    /// fleet's facts (<see cref="FleetFacts"/>). Nothing here is a secret, and nothing is written but the ids
    /// <see cref="Write"/> puts into the project.
    /// </summary>
    public sealed class ConnectState
    {
        /// <summary>The fleets <c>GET fleets</c> listed, or null before it answered.</summary>
        public IReadOnlyList<FleetView> Fleets { get; private set; }

        /// <summary>The picked fleet's facts, or null before they were read.</summary>
        public FleetFacts Facts { get; private set; }

        /// <summary>Why the last read failed, or null.</summary>
        public string Problem { get; private set; }

        /// <summary>How old the picked fleet's facts may grow before a focus of the window reads them again.</summary>
        public static readonly TimeSpan ReadAgainAfter = TimeSpan.FromSeconds(60);

        /// <summary>When the picked fleet was last read (a success or a failure), or null before the first read.</summary>
        public DateTime? LastReadUtc { get; private set; }

        /// <summary>
        /// True when a read finished at <paramref name="lastReadUtc"/> is old enough at <paramref name="nowUtc"/> to read again
        /// on focus, so a deployment added in the panel reaches the chips without a press. Never before the first read (the
        /// window's load does that one) and never for a clock that went backwards. Pure.
        /// </summary>
        public static bool ShouldReadAgain(DateTime? lastReadUtc, DateTime nowUtc)
        {
            if (!lastReadUtc.HasValue)
            {
                return false;
            }

            TimeSpan age = nowUtc - lastReadUtc.Value;
            return age >= ReadAgainAfter;
        }

        /// <summary>Lists the workspace's fleets for the picker.</summary>
        public async Task ListFleetsAsync(IPingCoreApi api, CancellationToken ct)
        {
            ApiResult<FleetListResponse> listed = await api.ListFleetsAsync(ct);
            if (!listed.Ok)
            {
                Problem = ErrorText.Of(listed.Error);
                return;
            }

            Problem = null;
            Fleets = (listed.Value.Fleets ?? new List<FleetView>()).Where(f => f != null && f.FleetId > 0).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Reads the fleet's detail, its game and its build targets into <see cref="Facts"/>; a failed read leaves
        /// <see cref="Problem"/>. When <paramref name="stillPicked"/> answers false once the reads are back (another fleet
        /// was picked meanwhile), the answer is dropped and nothing here changes, so an older read never undoes a pick.
        /// </summary>
        public async Task ResolveAsync(IPingCoreApi api, long fleetId, CancellationToken ct, Func<bool> stillPicked = null)
        {
            (FleetFacts facts, PluginError error) = await Read(api, fleetId, ct);
            if (stillPicked != null && !stillPicked())
            {
                return;
            }

            LastReadUtc = DateTime.UtcNow;
            if (facts == null)
            {
                Facts = null;
                Problem = ErrorText.Of(error);
                return;
            }

            Problem = null;
            Facts = facts;
        }

        /// <summary>Takes facts read elsewhere (a fleet just picked).</summary>
        public void Use(FleetFacts facts)
        {
            LastReadUtc = DateTime.UtcNow;
            Facts = facts;
            Problem = null;
        }

        /// <summary>Forgets the fleet's facts (another fleet was picked, or the key changed).</summary>
        public void Clear()
        {
            LastReadUtc = null;
            Facts = null;
            Problem = null;
        }

        /// <summary>
        /// <c>GET fleets/{id}</c>, then the fleet's game (<c>GET my-games/{id}</c>, key-only: Push resolves from its
        /// branches) and, for a fleet with deployments, <c>GET fleets/{id}/build-targets</c> (a fleet with none answers
        /// 400 there; only Release reads them). A failed detail read is an error; a failed game or build targets read
        /// becomes the facts' <see cref="FleetFacts.GameProblem"/> or <see cref="FleetFacts.ReleaseProblem"/>, in the
        /// workspace's words.
        /// </summary>
        public static async Task<(FleetFacts Facts, PluginError Error)> Read(IPingCoreApi api, long fleetId, CancellationToken ct)
        {
            ApiResult<FleetDetailResponse> detail = await api.GetFleetAsync(fleetId, ct);
            if (!detail.Ok)
            {
                return (null, detail.Error);
            }

            GameBranchesResponse game = null;
            PluginError gameError = null;
            long gameId = detail.Value.Fleet?.GameId ?? 0;
            if (gameId > 0)
            {
                ApiResult<GameBranchesResponse> read = await api.GetGameBranchesAsync(gameId, ct);
                game = read.Ok ? read.Value : null;
                gameError = read.Ok ? null : read.Error;
            }

            BuildTargetsResponse targets = null;
            PluginError targetsError = null;
            if ((detail.Value.Deployments ?? new List<FleetDeploymentView>()).Any(d => d != null))
            {
                ApiResult<BuildTargetsResponse> read = await api.ListBuildTargetsAsync(fleetId, ct);
                targets = read.Ok ? read.Value : null;
                targetsError = read.Ok ? null : read.Error;
            }

            return (FleetResolution.Resolve(fleetId, detail.Value, targets, targetsError, game, gameError), null);
        }

        /// <summary>
        /// Writes the picked fleet into the project: its fleet and game ids into <c>ProjectSettings/PingCoreEditor.json</c>,
        /// and its Discovery app's public id into the client settings asset (created under <c>Assets/PingCore/Resources/</c>
        /// when the project has none). A value already in place is not written again. Answers what it holds.
        /// </summary>
        public static string Write(string projectRoot, FleetFacts facts)
        {
            if (facts == null || facts.FleetId <= 0)
            {
                throw new ArgumentException("A fleet is required.", nameof(facts));
            }

            EditorProjectSettings project = EditorProjectSettings.Load(projectRoot);
            long gameId = facts.GameId > 0 ? facts.GameId : 0;
            if (project.FleetId != facts.FleetId || project.GameId != gameId)
            {
                // A branch or a server executable picked for another game means nothing here: Ship asks again (or takes
                // the only branch, or the only file the game launches).
                if (project.GameId != gameId)
                {
                    project.GameBranchId = 0;
                    project.ProcessName = null;
                }

                project.FleetId = facts.FleetId;
                project.GameId = gameId;
                project.Save(projectRoot);
            }

            string wrote = $"{EditorProjectSettings.RelativePath}: fleet #{facts.FleetId}, game #{project.GameId}";

            if (string.IsNullOrWhiteSpace(facts.AppPublicId))
            {
                return wrote + ". The fleet names no Discovery app the key can see, so the client settings asset was left as it is.";
            }

            PingCoreClientSettings asset = ClientSettingsAsset.FindOrCreate(out string path, out bool created);
            var serialized = new SerializedObject(asset);
            serialized.Update();
            SerializedProperty fleetApp = serialized.FindProperty("fleetAppPublicId");
            if (fleetApp != null && !string.Equals(fleetApp.stringValue, facts.AppPublicId.Trim(), StringComparison.Ordinal))
            {
                fleetApp.stringValue = facts.AppPublicId.Trim();
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(asset);
            }

            return wrote + $"; {path}{(created ? " (created)" : string.Empty)}: fleet app {facts.AppPublicId}.";
        }
    }
}
