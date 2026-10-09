using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Common;

namespace PingCore.Editor.Workspace.UI.Connect
{
    /// <summary>What a picked fleet answers for the whole window. Ids and names only.</summary>
    public sealed class FleetFacts
    {
        public long FleetId { get; set; }

        public string FleetName { get; set; }

        public long GameId { get; set; }

        public string GameName { get; set; }

        /// <summary>The fleet's private Discovery app's public id (<c>dscp_</c>), written into the client settings asset.</summary>
        public string AppPublicId { get; set; }

        public IReadOnlyList<FleetDeploymentView> Deployments { get; set; } = Array.Empty<FleetDeploymentView>();

        public int DeploymentCount => Deployments.Count;

        /// <summary>The data source the fleet's deployments deliver from, from its build targets (<c>cdn_source</c>, <c>image</c>, <c>steam_depot</c>), or null when unread (a fleet with no deployment has none).</summary>
        public string DataSource { get; set; }

        /// <summary>The CDN source every member deployment delivers from (build targets <c>cdnSourceId</c>), or 0. Release compares it with the source Push pushes to; Push never uses it.</summary>
        public long CdnSourceId { get; set; }

        /// <summary>Why Release cannot run for this fleet's deployments, or null (a fleet with no deployment is <see cref="UI.Ship.ReleaseGate"/>'s own case).</summary>
        public string ReleaseProblem { get; set; }

        /// <summary>The fleet's game and its branches (key-only), or null when not read.</summary>
        public GameBranchesResponse Game { get; set; }

        /// <summary>Why the game could not be read, or null.</summary>
        public string GameProblem { get; set; }

        /// <summary>The fleet's note for Status (no deployment yet: <see cref="FleetResolution.NoDeploymentMessage"/>), or null. Connect shows none.</summary>
        public string Note { get; set; }
    }

    /// <summary>
    /// A chosen fleet answers everything else (the hosting spec's table), pure: the game, the Discovery app's public
    /// id and the deployments come from <c>GET fleets/{id}</c>; the game's branches from <c>GET my-games/{id}</c> (Push
    /// resolves from them, never from deployments); and, for a fleet with deployments, the data source and CDN source
    /// those deployments deliver from come from <c>GET fleets/{id}/build-targets</c>, whose <c>cdnSourceId</c> is set
    /// only when every member delivers from the same CDN source. Only Release reads those: the plugin never guesses one,
    /// so a null id on a CDN fleet refuses Release with a sentence.
    /// </summary>
    public static class FleetResolution
    {
        /// <summary>Connect's line when the workspace has no fleet (the hosting spec's words).</summary>
        public const string NoFleetMessage = "Set up your game and a fleet in the panel first.";

        /// <summary>Status's line for a fleet with no deployment (the hosting spec's words; Connect is done without one).</summary>
        public const string NoDeploymentMessage = "Your fleet has no deployment, so no game servers are running. Add one in the panel.";

        /// <summary>A CDN fleet whose members deliver from different CDN sources (or none): Release refuses.</summary>
        public const string MembersDisagreeMessage = "The fleet's deployments do not all deliver from one CDN source, so the plugin will not release onto them. Give them the same CDN source in the panel.";

        /// <summary>A CDN fleet whose build targets carry no <c>cdnSourceId</c> at all: Release refuses.</summary>
        public const string NoCdnSourceFieldMessage = "This PingCore workspace does not say which CDN source the fleet's deployments deliver from, so the plugin will not release onto them. Release with MCP or in the panel until it does.";

        /// <summary>The trimmed <c>dscp_</c> public id, or null for anything else.</summary>
        public static string PublicIdOrNull(string value)
        {
            string trimmed = value?.Trim();
            return Confirmation.HeartbeatTokenConfirmationPolicy.IsPublicId(trimmed) ? trimmed : null;
        }

        /// <summary>
        /// The facts of fleet <paramref name="fleetId"/>, from its detail, its build targets (or the error reading them;
        /// both null for a fleet with no deployment) and its game's branches (or the error reading them).
        /// </summary>
        public static FleetFacts Resolve(long fleetId, FleetDetailResponse detail, BuildTargetsResponse targets, PluginError targetsError, GameBranchesResponse game = null, PluginError gameError = null)
        {
            FleetView fleet = detail?.Fleet;
            List<FleetDeploymentView> deployments = (detail?.Deployments ?? new List<FleetDeploymentView>()).Where(d => d != null).ToList();
            var facts = new FleetFacts
            {
                FleetId = fleet != null && fleet.FleetId > 0 ? fleet.FleetId : fleetId,
                FleetName = fleet?.Name,
                GameId = fleet?.GameId ?? 0,
                GameName = fleet?.GameName,
                // Only a dscp_ public id reaches the shipped client settings asset, never anything else the answer holds.
                AppPublicId = PublicIdOrNull(fleet?.DiscoveryPublicId),
                Deployments = deployments,
                Game = game,
                GameProblem = game == null && gameError != null ? "The fleet's game could not be read: " + ErrorText.Of(gameError) : null,
            };

            if (deployments.Count == 0)
            {
                // Status says so; Release refuses (ReleaseGate). Connect, Build and Push do not depend on deployments.
                facts.Note = NoDeploymentMessage;
                return facts;
            }

            if (targets == null)
            {
                facts.ReleaseProblem = "The fleet's build targets could not be read: " + (targetsError == null ? "no answer." : ErrorText.Of(targetsError)) + " Press Refresh under Connect.";
                return facts;
            }

            facts.DataSource = targets.DataSourceType;
            string refusal = DataSourceRule.Refusal(targets.DataSourceType);
            if (refusal != null)
            {
                facts.ReleaseProblem = refusal;
                return facts;
            }

            if (targets.CdnSourceId.HasValue && targets.CdnSourceId.Value > 0)
            {
                facts.CdnSourceId = targets.CdnSourceId.Value;
                return facts;
            }

            facts.ReleaseProblem = targets.CdnSourceIdSpecified ? MembersDisagreeMessage : NoCdnSourceFieldMessage;
            return facts;
        }

        /// <summary>A fleet picker entry: <c>Beacon Rush EU (Beacon Rush, 2 deployments)</c>. Pure.</summary>
        public static string Choice(FleetView fleet)
        {
            if (fleet == null)
            {
                return string.Empty;
            }

            int count = fleet.DeploymentCount ?? 0;
            string deployments = count == 1 ? "1 deployment" : count + " deployments";
            return $"{fleet.Name} ({fleet.GameName ?? "game #" + fleet.GameId}, {deployments}) #{fleet.FleetId}";
        }
    }
}
