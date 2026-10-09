using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Discovery.Client;
using PingCore.Editor.Workspace.Api.Wire;

namespace PingCore.Editor.Workspace.Infrastructure
{
    /// <summary>
    /// The exact reason behind a game client's missing-infrastructure message, read from the workspace, pure: what the
    /// fleet Connect picked says about the state the client found (<see cref="InfrastructureState"/>). One sentence, or
    /// null when the workspace adds nothing (the fleet read failed, or the state is not one the workspace explains). The
    /// live state is optional: <c>GET fleets/{id}/live</c> answers 503 while the fleet's app is disabled or has no active
    /// token, so everything the fleet read alone can say (no app id, the wrong app, no deployment, a failed one) is said
    /// without it, and only the ready, draining and wanted counts need it. It names fleets, deployments, counts and the
    /// fleet's public app id; never a key, a token or an error body.
    /// </summary>
    public static class InfrastructureDetail
    {
        private static readonly string[] Starting = { "pending", "deploying", "scaling" };
        private static readonly string[] Leaving = { "removing", "removed" };

        /// <summary>
        /// The detail for <paramref name="state"/>, from the picked fleet (<c>GET fleets/{id}</c>) and, when it could be
        /// read, its live state (<c>GET fleets/{id}/live</c>). Null when the fleet read is missing or the state is not one
        /// the workspace explains.
        /// </summary>
        /// <param name="state">What the game client found.</param>
        /// <param name="appPublicId">The app id the client checked, or null.</param>
        /// <param name="fleet">The picked fleet and its member deployments.</param>
        /// <param name="live">The fleet's live state, or null when it could not be read.</param>
        public static string Describe(InfrastructureState state, string appPublicId, FleetDetailResponse fleet, FleetLiveResponse live)
        {
            if (fleet?.Fleet == null)
            {
                return null;
            }

            string name = "Fleet " + FleetName(fleet.Fleet);
            string fleetApp = fleet.Fleet.DiscoveryPublicId;
            bool hasApp = DiscoveryClient.IsAppPublicId(fleetApp);
            bool otherApp = hasApp && !string.IsNullOrWhiteSpace(appPublicId) && !string.Equals(appPublicId.Trim(), fleetApp, StringComparison.Ordinal);
            switch (state)
            {
                case InfrastructureState.NoAppId:
                    return hasApp
                        ? name + " is picked in Window > PingCore, but the client settings asset this game reads has no app id. Connect writes " + fleetApp + " into the project's settings asset; check that this scene uses that asset, or pick the fleet again."
                        : name + " is picked in Window > PingCore, but it has no Discovery app. Link one to the fleet in the panel.";
                case InfrastructureState.AppUnknown:
                    if (!hasApp)
                    {
                        return name + " has no Discovery app. Link one to the fleet in the panel.";
                    }

                    return otherApp
                        ? "This game's app id is not the Discovery app of " + name + " (" + fleetApp + "). Pick the fleet again in Window > PingCore, Connect."
                        : "The Discovery app of " + name + " (" + fleetApp + ") is unknown to Discovery or disabled. Check the app on the panel's Discovery page.";
                case InfrastructureState.NoGameServers:
                    return otherApp
                        ? "This game asks another Discovery app than the one of " + name + " (" + fleetApp + "). Pick the fleet again in Window > PingCore, Connect."
                        : Deployments(name, fleet.Deployments, live);
                default:
                    return null;
            }
        }

        /// <summary>Why a fleet whose app Discovery knows lists no game server, from its deployments and, if read, their live counts. Pure.</summary>
        internal static string Deployments(string name, IReadOnlyList<FleetDeploymentView> deployments, FleetLiveResponse live)
        {
            List<FleetDeploymentView> members = (deployments ?? Array.Empty<FleetDeploymentView>()).Where(d => d != null).ToList();
            if (members.Count == 0)
            {
                return name + " has no deployment.";
            }

            List<FleetLiveDeploymentView> counts = live == null
                ? new List<FleetLiveDeploymentView>()
                : members
                    .Select(d => live.Deployments?.FirstOrDefault(l => l != null && l.BrandDeploymentId == d.BrandDeploymentId))
                    .Where(l => l != null)
                    .ToList();
            int ready = counts.Sum(l => Math.Max(0, l.Ready));
            if (ready > 0)
            {
                // Ready game servers win over a failed sibling: Discovery only lags behind them.
                return name + " has " + Plural(ready, "game server") + " ready; Discovery lists a game server a few seconds after it calls ready. Check again.";
            }

            FleetDeploymentView failed = members.FirstOrDefault(d => Is(d.DeploymentStatus, "failed"));
            if (failed != null)
            {
                return "Deployment " + DeploymentName(failed) + " of " + name + " failed.";
            }

            if (members.All(d => Leaving.Any(s => Is(d.DeploymentStatus, s))))
            {
                return name + ": " + Its(members.Count, "being removed") + ".";
            }

            bool starting = members.Any(d => Starting.Any(s => Is(d.DeploymentStatus, s)));
            int wanted = Math.Max(members.Sum(d => Math.Max(Math.Max(0, d.ServerCount), Math.Max(0, d.MinServers))), counts.Sum(l => Math.Max(0, l.Total)));
            if (live == null)
            {
                return starting
                    ? name + ": " + Its(members.Count, "starting") + "."
                    : name + ": " + Its(members.Count, "running") + " (" + Plural(wanted, "game server") + "), but its live state could not be read: the fleet's Discovery app may be disabled or have no active token. Check the app on the panel's Discovery page.";
            }

            if (starting)
            {
                return name + ": " + Its(members.Count, "starting") + " (0 of " + wanted + " ready).";
            }

            if (wanted == 0)
            {
                return name + ": " + Its(members.Count, "scaled to 0 game servers") + ". Raise the minimum in the panel.";
            }

            int draining = counts.Sum(l => Math.Max(0, l.Draining));
            return draining > 0
                ? name + ": " + Its(members.Count, "running") + ", but no game server is ready (0 of " + wanted + " ready, " + draining + " draining). A game server reads draining until the game calls ready."
                : name + ": " + Its(members.Count, "running") + ", but no game server is ready yet (0 of " + wanted + " ready). Ship a build if none has been released.";
        }

        private static string Its(int count, string what) => count == 1 ? "its deployment is " + what : "its " + count + " deployments are " + what;

        private static string Plural(int n, string what) => n == 1 ? "1 " + what : n + " " + what + "s";

        private static bool Is(string status, string wanted) => string.Equals(status, wanted, StringComparison.OrdinalIgnoreCase);

        private static string FleetName(FleetView fleet) => string.IsNullOrWhiteSpace(fleet.Name) ? "#" + fleet.FleetId : fleet.Name.Trim();

        private static string DeploymentName(FleetDeploymentView d)
        {
            if (!string.IsNullOrWhiteSpace(d.FriendlyName))
            {
                return d.FriendlyName.Trim();
            }

            return !string.IsNullOrWhiteSpace(d.LocationName) ? d.LocationName.Trim() : "#" + d.BrandDeploymentId;
        }
    }
}
