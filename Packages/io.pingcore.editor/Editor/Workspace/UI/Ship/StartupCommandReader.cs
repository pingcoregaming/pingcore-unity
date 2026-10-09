using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Common;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// Reads the game's startup command for the Push check and the Build row's executable, never from a branch's default
    /// deployment spec (a legacy setting: everything runs through deployments, and many deployments can use one
    /// branch).
    /// <list type="bullet">
    /// <item>A fleet WITH deployments: each member deployment (<c>GET brand/servers/deployments/{id}</c>, key-only) names
    /// its spec, the game's specs (<c>GET my-games/{id}/kubernetes/deployment-specs</c>) name each spec's template set,
    /// and each distinct template set (<c>GET my-games/{gameId}/template-sets/{id}</c>) names the process its active
    /// command-line config launches. The build must hold every member's file (<see cref="StartupCommand.ForDeployments"/>).</item>
    /// <item>A fleet with NO deployment: the game's template sets (from the key-only game read, <c>GET my-games/{id}</c>
    /// <c>templateSets</c>; the API has no template set list route) and each one's command-line config. The build
    /// must hold any one of their files (<see cref="StartupCommand.ForTemplateSets"/>).</item>
    /// </list>
    /// A deployment or template set the workspace answers 404 for is left out with the reason; any other refused read
    /// stops Push in the workspace's own words. The decisions are pure; this class only reads.
    /// </summary>
    public static class StartupCommandReader
    {
        /// <summary>The startup command check for the fleet and game <paramref name="target"/> resolved from.</summary>
        public static Task<StartupCheck> ReadAsync(IPingCoreApi api, PushTarget target, CancellationToken ct)
        {
            return ReadAsync(api, target?.GameId ?? 0, target?.Deployments, target?.TemplateSets, ct);
        }

        /// <summary>
        /// The startup command check for game <paramref name="gameId"/>: from <paramref name="deployments"/> (the fleet's
        /// members) when there are any, else from <paramref name="templateSets"/> (the game's, null when the game was not read).
        /// </summary>
        public static async Task<StartupCheck> ReadAsync(IPingCoreApi api, long gameId, IReadOnlyList<FleetDeploymentView> deployments, IReadOnlyList<GameTemplateSetView> templateSets, CancellationToken ct)
        {
            if (gameId <= 0)
            {
                return StartupCheck.Refused("The fleet names no game, so its startup command cannot be read.");
            }

            // Each deployment once: GET brand/servers/deployments/{id} is rate limited per brand member (30 a minute).
            List<FleetDeploymentView> members = (deployments ?? new List<FleetDeploymentView>())
                .Where(d => d != null && d.BrandDeploymentId > 0)
                .GroupBy(d => d.BrandDeploymentId)
                .Select(g => g.First())
                .ToList();
            if (members.Count > 0)
            {
                return await FromDeploymentsAsync(api, gameId, members, ct);
            }

            if (templateSets == null)
            {
                return StartupCheck.Skip("the fleet has no deployment and its game was not read, so the game's template sets are not known. Press Refresh under Connect.");
            }

            var sources = new List<StartupSource>();
            foreach (GameTemplateSetView set in templateSets.Where(s => s != null && s.TemplateSetId > 0))
            {
                string label = "template set " + SetName(set.SetName, set.TemplateSetId);
                (TemplateSetResponse read, string gone, StartupCheck refused) = await ReadSetAsync(api, gameId, set.TemplateSetId, ct);
                if (refused != null)
                {
                    return refused;
                }

                sources.Add(new StartupSource { Label = label, TemplateSet = read, Unresolved = gone == null ? null : label + " no longer exists" });
            }

            return StartupCommand.ForTemplateSets(sources);
        }

        private static async Task<StartupCheck> FromDeploymentsAsync(IPingCoreApi api, long gameId, IReadOnlyList<FleetDeploymentView> members, CancellationToken ct)
        {
            // Each member's spec, from the deployment read (the fleet's own answer does not carry it).
            var specOf = new Dictionary<long, long>();
            var missing = new Dictionary<long, string>();
            foreach (FleetDeploymentView member in members)
            {
                ApiResult<DeploymentReadResponse> read = await api.GetDeploymentAsync(member.BrandDeploymentId, ct);
                if (!read.Ok && read.Error.Kind == PluginErrorKind.NotFound)
                {
                    missing[member.BrandDeploymentId] = "it no longer exists";
                    continue;
                }

                if (!read.Ok)
                {
                    return Unreadable(read.Error);
                }

                specOf[member.BrandDeploymentId] = read.Value?.Deployment?.DeploymentSpecId ?? 0;
            }

            Dictionary<long, DeploymentSpecView> specs = new Dictionary<long, DeploymentSpecView>();
            if (specOf.Values.Any(id => id > 0))
            {
                ApiResult<DeploymentSpecListResponse> list = await api.ListDeploymentSpecsAsync(gameId, ct);
                if (!list.Ok)
                {
                    return Unreadable(list.Error);
                }

                foreach (DeploymentSpecView spec in (list.Value.Specs ?? new List<DeploymentSpecView>()).Where(s => s != null && s.SpecId > 0))
                {
                    specs[spec.SpecId] = spec;
                }
            }

            // Each distinct template set once, in the members' order.
            var sets = new Dictionary<long, (TemplateSetResponse Set, string Gone)>();
            var sources = new List<StartupSource>();
            foreach (FleetDeploymentView member in members)
            {
                var source = new StartupSource { Label = "deployment " + MemberName(member) };
                sources.Add(source);
                if (missing.TryGetValue(member.BrandDeploymentId, out string gone))
                {
                    source.Unresolved = gone;
                    continue;
                }

                long specId = specOf[member.BrandDeploymentId];
                if (specId <= 0)
                {
                    source.Unresolved = "it names no deployment spec";
                    continue;
                }

                if (!specs.TryGetValue(specId, out DeploymentSpecView spec))
                {
                    source.Unresolved = $"its deployment spec #{specId} is not among the game's specs";
                    continue;
                }

                if (spec.TemplateSetId == null || spec.TemplateSetId.Value <= 0)
                {
                    source.Unresolved = $"its deployment spec {SetName(spec.SpecName, spec.SpecId)} names no template set";
                    continue;
                }

                long setId = spec.TemplateSetId.Value;
                if (!sets.TryGetValue(setId, out (TemplateSetResponse Set, string Gone) known))
                {
                    (TemplateSetResponse read, string setGone, StartupCheck refused) = await ReadSetAsync(api, gameId, setId, ct);
                    if (refused != null)
                    {
                        return refused;
                    }

                    known = (read, setGone);
                    sets[setId] = known;
                }

                source.TemplateSet = known.Set;
                source.Unresolved = known.Gone;
            }

            return StartupCommand.ForDeployments(sources);
        }

        // One template set: the answer, or why it is gone (a 404), or the refusal that stops Push.
        private static async Task<(TemplateSetResponse Set, string Gone, StartupCheck Refused)> ReadSetAsync(IPingCoreApi api, long gameId, long templateSetId, CancellationToken ct)
        {
            ApiResult<TemplateSetResponse> set = await api.GetTemplateSetAsync(gameId, templateSetId, ct);
            if (!set.Ok && set.Error.Kind == PluginErrorKind.NotFound)
            {
                return (null, $"its template set #{templateSetId} no longer exists", null);
            }

            return set.Ok ? (set.Value, null, null) : (null, null, Unreadable(set.Error));
        }

        private static StartupCheck Unreadable(PluginError error) => StartupCheck.Refused("The startup command could not be read: " + ErrorText.Of(error));

        private static string MemberName(FleetDeploymentView member)
        {
            return string.IsNullOrWhiteSpace(member.FriendlyName) ? "#" + member.BrandDeploymentId : member.FriendlyName.Trim();
        }

        private static string SetName(string name, long id) => string.IsNullOrWhiteSpace(name) ? "#" + id : name.Trim();
    }
}
