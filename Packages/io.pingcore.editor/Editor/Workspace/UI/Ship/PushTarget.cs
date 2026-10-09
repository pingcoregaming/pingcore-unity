using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Connect;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>Where Push stands for the branch it would push to.</summary>
    public enum PushTargetKind
    {
        /// <summary>The fleet's game was not read (yet), or the fleet names no game.</summary>
        NotRead,

        /// <summary>The game has no branch.</summary>
        NoBranches,

        /// <summary>The game has several branches and none is picked.</summary>
        PickBranch,

        /// <summary>The branch has no data source (<c>none</c>).</summary>
        NoDataSource,

        /// <summary>The branch runs a container image.</summary>
        Image,

        /// <summary>The branch gets its files from Steam.</summary>
        Steam,

        /// <summary>The branch is on <c>cdn_source</c> but names no CDN source.</summary>
        CdnWithoutSource,

        /// <summary>The old per-branch CDN (<c>cdn_legacy</c>, used by app-based game servers).</summary>
        LegacyCdn,

        /// <summary>A data source the plugin does not know.</summary>
        Other,

        /// <summary>The branch delivers from a CDN source: Push may run.</summary>
        Cdn,
    }

    /// <summary>The branch Push pushes to and what it says about it. Ids and names only.</summary>
    public sealed class PushTarget
    {
        public PushTargetKind Kind { get; set; }

        public long GameId { get; set; }

        /// <summary>The game's branches, in the answer's order.</summary>
        public IReadOnlyList<GameBranchView> Branches { get; set; } = Array.Empty<GameBranchView>();

        /// <summary>The branch Push pushes to, or null (no branch, or several and none picked).</summary>
        public GameBranchView Branch { get; set; }

        /// <summary>The branch's CDN source; above 0 only for <see cref="PushTargetKind.Cdn"/>.</summary>
        public long CdnSourceId { get; set; }

        /// <summary>The line under the branch picker: "Pushes to CDN source #31", or why Push cannot run.</summary>
        public string Line { get; set; }

        public bool CanPush => Kind == PushTargetKind.Cdn && CdnSourceId > 0;

        /// <summary>The branch's files come from an image or Steam: Push happens outside the plugin.</summary>
        public bool OutsideThePlugin => Kind == PushTargetKind.Image || Kind == PushTargetKind.Steam;

        /// <summary>Why Push cannot run, or null.</summary>
        public string Problem => CanPush ? null : Line;

        /// <summary>A warning that does not stop Push (a branch not for Linux), or null.</summary>
        public string Warning { get; set; }

        /// <summary>
        /// The fleet's member deployments (<c>GET fleets/{id}</c>), whatever the branch: the startup command is read from
        /// their specs' template sets (<see cref="StartupCommandReader"/>). Never decides where Push goes.
        /// </summary>
        public IReadOnlyList<FleetDeploymentView> Deployments { get; set; } = Array.Empty<FleetDeploymentView>();

        /// <summary>The game's template sets (key-only game read), or null when the game was not read: a fleet with no deployment reads its startup command from them.</summary>
        public IReadOnlyList<GameTemplateSetView> TemplateSets { get; set; }
    }

    /// <summary>
    /// Push resolves from the fleet's GAME, never from its deployments: the game's branches
    /// (<c>GET my-games/{id}</c>, key-only) each name a data source and, on <c>cdn_source</c>, the CDN source Push uploads
    /// to. The branch is the one the project remembers (<c>ProjectSettings/PingCoreEditor.json</c> <c>gameBranchId</c>),
    /// else the game's only branch; with several and none remembered the developer picks. Pure.
    /// </summary>
    public static class PushTargetResolution
    {
        /// <summary>The game has no branch.</summary>
        public const string NoBranchesMessage = "The fleet's game has no branch, so there is nowhere to push. Add a branch to the game in the panel.";

        /// <summary>Several branches and none picked.</summary>
        public const string PickBranchMessage = "The game has several branches: pick the branch to push to.";

        /// <summary>The game was not read yet.</summary>
        public const string NotReadMessage = "The fleet's game is not read yet, so the branch to push to is not known. Press Refresh under Connect.";

        /// <summary>The fleet names no game.</summary>
        public const string NoGameMessage = "The fleet names no game, so there is no branch to push to. Check the fleet in the panel.";

        /// <summary>"Branch Main has no data source, ...".</summary>
        public static string NoDataSourceMessage(string branch) => $"Branch {branch} has no data source, so there is nowhere to push. Set its data source to a CDN source in the panel.";

        /// <summary>"Branch Main gets its files from Steam, ...".</summary>
        public static string SteamMessage(string branch) => $"Branch {branch} gets its files from Steam, and the plugin pushes to CDN sources only.";

        /// <summary>A CDN branch that names no source.</summary>
        public static string CdnWithoutSourceMessage(string branch) => $"Branch {branch} delivers from a CDN source but names none, so there is nowhere to push. Pick its CDN source in the panel.";

        /// <summary>The legacy per-branch CDN.</summary>
        public static string LegacyCdnMessage(string branch) => $"Branch {branch} uses the legacy CDN, which the plugin does not push to. Set its data source to a CDN source in the panel.";

        /// <summary>A branch whose platform is not Linux: Ship builds a Linux dedicated server.</summary>
        public static string PlatformWarning(string branch, string platform) => $"Branch {branch} is for {platform}, but Ship builds a Linux dedicated server; check the branch's platform in the panel.";

        /// <summary>A data source the plugin does not know.</summary>
        public static string OtherMessage(string branch, string dataSourceType) => $"Branch {branch} gets its files from '{dataSourceType}', and the plugin pushes to CDN sources only. Set its data source to a CDN source in the panel.";

        /// <summary>The line for a branch Push can push to.</summary>
        public static string PushesTo(long cdnSourceId) => $"Pushes to CDN source #{cdnSourceId}.";

        /// <summary>
        /// The push target of a picked fleet (<paramref name="facts"/>, null before Connect read it): its game's branches
        /// and the remembered branch id. A fleet with no game, or a game that could not be read, says so.
        /// </summary>
        public static PushTarget ForFleet(FleetFacts facts, long rememberedBranchId)
        {
            if (facts == null)
            {
                return Resolve(null, rememberedBranchId);
            }

            if (facts.GameId <= 0)
            {
                return new PushTarget { Kind = PushTargetKind.NotRead, Line = NoGameMessage, Deployments = facts.Deployments };
            }

            if (facts.Game == null)
            {
                return new PushTarget { Kind = PushTargetKind.NotRead, GameId = facts.GameId, Line = facts.GameProblem ?? NotReadMessage, Deployments = facts.Deployments };
            }

            PushTarget target = Resolve(facts.Game, rememberedBranchId);
            target.GameId = target.GameId > 0 ? target.GameId : facts.GameId;
            target.Deployments = facts.Deployments ?? Array.Empty<FleetDeploymentView>();
            return target;
        }

        /// <summary>The push target for <paramref name="game"/> (null: not read) and the remembered branch id (0: none).</summary>
        public static PushTarget Resolve(GameBranchesResponse game, long rememberedBranchId)
        {
            if (game == null)
            {
                return new PushTarget { Kind = PushTargetKind.NotRead, Line = NotReadMessage };
            }

            List<GameBranchView> branches = (game.GameBranches ?? new List<GameBranchView>()).Where(b => b != null && b.GameBranchId > 0).ToList();
            var target = new PushTarget
            {
                GameId = game.GameId,
                Branches = branches,
                TemplateSets = (game.TemplateSets ?? new List<GameTemplateSetView>()).Where(s => s != null && s.TemplateSetId > 0).ToList(),
            };
            if (branches.Count == 0)
            {
                target.Kind = PushTargetKind.NoBranches;
                target.Line = NoBranchesMessage;
                return target;
            }

            GameBranchView branch = branches.FirstOrDefault(b => rememberedBranchId > 0 && b.GameBranchId == rememberedBranchId)
                ?? (branches.Count == 1 ? branches[0] : null);
            if (branch == null)
            {
                target.Kind = PushTargetKind.PickBranch;
                target.Line = PickBranchMessage;
                return target;
            }

            target.Branch = branch;
            string name = Name(branch);
            string platform = branch.Platform?.Trim();
            target.Warning = string.IsNullOrEmpty(platform) || string.Equals(platform, "linux", StringComparison.OrdinalIgnoreCase) ? null : PlatformWarning(name, platform);
            switch (branch.DataSourceType)
            {
                case DataSourceRule.Cdn:
                    if (branch.CdnSourceId.HasValue && branch.CdnSourceId.Value > 0)
                    {
                        target.Kind = PushTargetKind.Cdn;
                        target.CdnSourceId = branch.CdnSourceId.Value;
                        target.Line = PushesTo(target.CdnSourceId);
                    }
                    else
                    {
                        target.Kind = PushTargetKind.CdnWithoutSource;
                        target.Line = CdnWithoutSourceMessage(name);
                    }

                    return target;
                case DataSourceRule.Image:
                    target.Kind = PushTargetKind.Image;
                    target.Line = DataSourceRule.ImageMessage;
                    return target;
                case DataSourceRule.Steam:
                    target.Kind = PushTargetKind.Steam;
                    target.Line = SteamMessage(name);
                    return target;
                case "cdn_legacy":
                    target.Kind = PushTargetKind.LegacyCdn;
                    target.Line = LegacyCdnMessage(name);
                    return target;
                case null:
                case "":
                case "none":
                    target.Kind = PushTargetKind.NoDataSource;
                    target.Line = NoDataSourceMessage(name);
                    return target;
                default:
                    target.Kind = PushTargetKind.Other;
                    target.Line = OtherMessage(name, branch.DataSourceType);
                    return target;
            }
        }

        /// <summary>A branch as the picker lists it, by the name the panel shows: <c>Main (linux, CDN source #31)</c>. Pure.</summary>
        public static string Choice(GameBranchView branch)
        {
            if (branch == null)
            {
                return string.Empty;
            }

            string platform = string.IsNullOrWhiteSpace(branch.Platform) ? "no platform" : branch.Platform.Trim();
            return $"{Name(branch)} ({platform}, {Source(branch)})";
        }

        /// <summary>
        /// The picker's entries for <paramref name="branches"/>, in order: <see cref="Choice"/>, with <c> #id</c> added only
        /// to entries that would otherwise read the same, so each entry picks exactly one branch. Pure.
        /// </summary>
        public static IReadOnlyList<string> Choices(IReadOnlyList<GameBranchView> branches)
        {
            List<string> plain = (branches ?? new List<GameBranchView>()).Select(Choice).ToList();
            var counts = plain.GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            return plain.Select((c, i) => counts[c] > 1 ? c + " #" + (branches[i]?.GameBranchId ?? 0) : c).ToList();
        }

        /// <summary>The branch's name as the panel shows it (<see cref="GameBranchView.DisplayName"/>), or <c>#id</c> when it has none.</summary>
        public static string Name(GameBranchView branch)
        {
            return branch == null ? "#0" : branch.DisplayName();
        }

        private static string Source(GameBranchView branch)
        {
            switch (branch.DataSourceType)
            {
                case DataSourceRule.Cdn:
                    return branch.CdnSourceId.HasValue && branch.CdnSourceId.Value > 0 ? "CDN source #" + branch.CdnSourceId.Value : "CDN source not set";
                case DataSourceRule.Image:
                    return "container image";
                case DataSourceRule.Steam:
                    return "Steam";
                case "cdn_legacy":
                    return "legacy CDN";
                case null:
                case "":
                case "none":
                    return "no data source";
                default:
                    return branch.DataSourceType;
            }
        }
    }
}
