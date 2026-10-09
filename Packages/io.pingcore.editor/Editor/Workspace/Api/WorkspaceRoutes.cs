using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PingCore.Editor.Workspace.Api
{
    /// <summary>Every PingCore API operation the plugin knows.</summary>
    public enum WorkspaceRouteId
    {
        ListFleets,
        GetFleet,
        GetFleetLive,
        ListBuildTargets,
        CreateRelease,
        GetRelease,
        CancelRelease,
        AcknowledgeRelease,
        IssuePushToken,
        ListDiscoveryApps,
        GetDiscoveryApp,
        PushInfo,
        PushPublish,
        PushStatus,
        GetCapabilities,
        GetGame,
        ListDeploymentSpecs,
        GetTemplateSet,
        CheckPushToken,
        GetDeployment,
    }

    /// <summary>Who sends a route's requests.</summary>
    public enum WorkspaceRouteCaller
    {
        /// <summary><see cref="PingCoreApiClient"/>, with the brand member's <c>usr_</c> key.</summary>
        Plugin,

        /// <summary><c>pingctl</c>, with the source's <c>cdnpush_</c> token; the plugin never sends these itself.</summary>
        Pingctl,

        /// <summary>
        /// <see cref="PingCoreApiClient"/>, with a push token the developer pasted instead of the brand member's key: only
        /// "Use an existing push token" checking which CDN source the token belongs to before it is kept.
        /// </summary>
        PluginWithPushToken,
    }

    /// <summary>One row of the route table.</summary>
    public sealed class WorkspaceRoute
    {
        internal WorkspaceRoute(WorkspaceRouteId id, string method, string template, string permission, string step, WorkspaceRouteCaller caller)
        {
            Id = id;
            Method = method;
            Template = template;
            Permission = permission;
            Step = step;
            Caller = caller;
        }

        public WorkspaceRouteId Id { get; }

        /// <summary>HTTP method in upper case.</summary>
        public string Method { get; }

        /// <summary>The path under <c>/api/</c> with placeholders, for example <c>fleets/{id}/releases</c>. A DTO's <c>[WireContract]</c> path is this with a leading slash.</summary>
        public string Template { get; }

        /// <summary>The brand permission the handler requires, or null for a <c>cdnpush_</c> route (either caller) and for the routes that need none (<see cref="WorkspaceRoutes.NoBrandPermission"/>).</summary>
        public string Permission { get; }

        /// <summary>The plugin step that uses the route.</summary>
        public string Step { get; }

        public WorkspaceRouteCaller Caller { get; }

        /// <summary>Fills the template's placeholders in order: <c>fleets/{fleetId}/releases/{releaseId}</c> with (1, 12).</summary>
        public string PathFor(params long[] ids)
        {
            string[] parts = Template.Split('/');
            int next = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("{", StringComparison.Ordinal))
                {
                    if (ids == null || next >= ids.Length || ids[next] <= 0)
                    {
                        throw new ArgumentException($"{Template} needs a positive id for {parts[i]}.", nameof(ids));
                    }

                    parts[i] = ids[next++].ToString(CultureInfo.InvariantCulture);
                }
            }

            if (ids != null && next != ids.Length)
            {
                throw new ArgumentException($"{Template} takes {next} id(s), not {ids.Length}.", nameof(ids));
            }

            return string.Join("/", parts);
        }
    }

    /// <summary>
    /// The plugin's closed route table: every PingCore API route it or <c>pingctl</c> calls (method and
    /// path), the brand permission each needs and the step that uses it. A contract checker compares the
    /// table with the API contract snapshots (not published), from the route table the editor DTO dump
    /// carries. A 403 is reported as the row's permission, never parsed from text. Every plugin row but
    /// the release calls and the push token issue is a read: the plugin creates nothing else in the
    /// workspace, and never edits a fleet. Three reads answer with fields the plugin has no use for,
    /// some of them sensitive, so their DTOs model only the keys the plugin reads and the client drops
    /// the rest unread: the game (<c>GET my-games/{id}</c>), the push info a pasted push token is checked
    /// with (<c>GET cdn-sources/push/info</c>) and a member deployment (<c>GET brand/servers/deployments/{id}</c>).
    /// </summary>
    public static class WorkspaceRoutes
    {
        /// <summary>The plugin routes whose handler checks no brand permission: <c>me/capabilities</c> needs a login only.</summary>
        public static IReadOnlyList<WorkspaceRouteId> NoBrandPermission { get; } = new[] { WorkspaceRouteId.GetCapabilities };

        /// <summary>Every row, in a stable order.</summary>
        public static IReadOnlyList<WorkspaceRoute> All { get; } = new[]
        {
            Row(WorkspaceRouteId.ListFleets, "GET", "fleets", "fleets.view", "verify-key"),
            Row(WorkspaceRouteId.GetCapabilities, "GET", "me/capabilities", null, "verify-key"),
            Row(WorkspaceRouteId.GetFleet, "GET", "fleets/{id}", "fleets.view", "connect"),
            Row(WorkspaceRouteId.GetFleetLive, "GET", "fleets/{id}/live", "fleets.view", "live-state"),
            Row(WorkspaceRouteId.ListBuildTargets, "GET", "fleets/{id}/build-targets", "fleets.view", "build-targets"),
            Row(WorkspaceRouteId.CreateRelease, "POST", "fleets/{id}/releases", "fleets.manage", "release"),
            Row(WorkspaceRouteId.GetRelease, "GET", "fleets/{fleetId}/releases/{releaseId}", "fleets.view", "release"),
            Row(WorkspaceRouteId.CancelRelease, "POST", "fleets/{fleetId}/releases/{releaseId}/cancel", "fleets.manage", "release"),
            Row(WorkspaceRouteId.AcknowledgeRelease, "POST", "fleets/{fleetId}/releases/{releaseId}/acknowledge", "fleets.manage", "release"),
            Row(WorkspaceRouteId.IssuePushToken, "POST", "cdn-sources/sources/{id}/push-token", "cdn-sources.edit", "push-token"),
            Row(WorkspaceRouteId.PushInfo, "GET", "cdn-sources/push/info", null, "push", WorkspaceRouteCaller.Pingctl),
            Row(WorkspaceRouteId.PushPublish, "POST", "cdn-sources/push/publish", null, "push", WorkspaceRouteCaller.Pingctl),
            Row(WorkspaceRouteId.PushStatus, "GET", "cdn-sources/push/status", null, "push", WorkspaceRouteCaller.Pingctl),

            // "Use an existing push token": which CDN source a pasted token belongs to, sent with that token.
            Row(WorkspaceRouteId.CheckPushToken, "GET", "cdn-sources/push/info", null, "push-token", WorkspaceRouteCaller.PluginWithPushToken),

            // Push resolves from the fleet's game: its branches (data source, CDN source) and template sets, key-only.
            Row(WorkspaceRouteId.GetGame, "GET", "my-games/{id}", "my-games.view", "ship"),

            // The Push check's startup command, never from a branch's default spec: each member deployment's spec
            // (key-only), the game's specs (each names its template set), and a template set's command-line config.
            Row(WorkspaceRouteId.GetDeployment, "GET", "brand/servers/deployments/{id}", "brand.servers.view", "ship"),
            Row(WorkspaceRouteId.ListDeploymentSpecs, "GET", "my-games/{id}/kubernetes/deployment-specs", "my-games.view", "startup-command"),
            Row(WorkspaceRouteId.GetTemplateSet, "GET", "my-games/{gameId}/template-sets/{templateSetId}", "my-games.templates", "startup-command"),

            // The community app of Player hosting and Confirm for build.
            Row(WorkspaceRouteId.ListDiscoveryApps, "GET", "discovery/apps", "discovery.view", "registration-mode"),
            Row(WorkspaceRouteId.GetDiscoveryApp, "GET", "discovery/apps/{id}", "discovery.view", "registration-mode"),
        };

        /// <summary>The row of <paramref name="id"/>.</summary>
        public static WorkspaceRoute Get(WorkspaceRouteId id) => All.Single(r => r.Id == id);

        private static WorkspaceRoute Row(WorkspaceRouteId id, string method, string template, string permission, string step, WorkspaceRouteCaller caller = WorkspaceRouteCaller.Plugin)
        {
            return new WorkspaceRoute(id, method, template, permission, step, caller);
        }
    }
}
