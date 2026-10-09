using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;

namespace PingCore.Editor.Workspace.Tests.Api
{
    /// <summary>
    /// The route table's own rules: the brand permission each row names, the routes that are never on
    /// it, the key-only reads and the path filling. Which API route each row is, is checked against the
    /// API contract snapshots (not published) by a contract checker, from the route table the editor
    /// DTO dump carries (<see cref="Contracts.WorkspaceDtoDumpTests"/>).
    /// </summary>
    public sealed class WorkspaceRoutesTests
    {
        [Test]
        public void EveryPluginRouteNamesABrandPermissionAndEveryPingctlRouteNone()
        {
            foreach (WorkspaceRoute route in WorkspaceRoutes.All)
            {
                if (route.Caller == WorkspaceRouteCaller.Plugin && WorkspaceRoutes.NoBrandPermission.Contains(route.Id))
                {
                    Assert.That(route.Permission, Is.Null, route.Template + " needs no brand permission, so a 403 names none");
                }
                else if (route.Caller == WorkspaceRouteCaller.Plugin)
                {
                    Assert.That(route.Permission, Does.Match("^[a-z-]+\\.[a-z.-]+$"), route.Template);
                }
                else
                {
                    Assert.That(route.Permission, Is.Null, route.Template);
                }
            }

            Assert.That(WorkspaceRoutes.All.Select(r => r.Id).Distinct().Count(), Is.EqualTo(WorkspaceRoutes.All.Count));
            Assert.That(WorkspaceRoutes.NoBrandPermission, Is.EquivalentTo(new[] { WorkspaceRouteId.GetCapabilities }), "only sign-in's name needs no brand permission");
        }

        [Test]
        public void TheGameListAndTheContainerListAndTheCdnSourceReadsAreNeverOnTheTable()
        {
            // Their answers carry fields the plugin has no use for, some of them sensitive.
            Assert.That(WorkspaceRoutes.All.Where(r => r.Template == "my-games" || r.Template.StartsWith("my-games/{id}/kubernetes/containers", System.StringComparison.Ordinal)).Select(r => r.Template), Is.Empty);
            Assert.That(WorkspaceRoutes.All.Where(r => r.Template.StartsWith("cdn-sources/categories", System.StringComparison.Ordinal)), Is.Empty,
                "the CDN source comes from the game's branch, never from a CDN source list");
        }

        [Test]
        public void TheGameReadTheDeploymentReadAndThePushInfoCheckAreKeyOnlyReads()
        {
            // GET my-games/{id} is read for Push, because it is the read that names each branch's data source and CDN
            // source. The answer carries fields the plugin has no use for, some of them sensitive; the client keeps only the
            // modelled keys and drops the rest unread. The same holds for the push info a pasted push token is checked with.
            Assert.That(WorkspaceRoutes.All.Where(r => r.Template == "my-games/{id}").Select(r => (r.Id, r.Method, r.Step, r.Permission)),
                Is.EqualTo(new[] { (WorkspaceRouteId.GetGame, "GET", "ship", "my-games.view") }));
            Assert.That(WorkspaceRoutes.Get(WorkspaceRouteId.CheckPushToken).Caller, Is.EqualTo(WorkspaceRouteCaller.PluginWithPushToken));
            Assert.That(WorkspaceRoutes.Get(WorkspaceRouteId.CheckPushToken).Permission, Is.Null, "the token is the credential, not a brand permission");

            // The member deployment read (its spec, for the startup command): the rest of the answer is dropped unread.
            Assert.That(WorkspaceRoutes.All.Where(r => r.Template == "brand/servers/deployments/{id}").Select(r => (r.Id, r.Method, r.Step, r.Permission)),
                Is.EqualTo(new[] { (WorkspaceRouteId.GetDeployment, "GET", "ship", "brand.servers.view") }));

            // Each key-only DTO models exactly these keys, so a key added to one fails here until it is reviewed.
            var modelled = new Dictionary<System.Type, string[]>
            {
                [typeof(global::PingCore.Editor.Workspace.Api.Wire.GameBranchesResponse)] = new[] { "gameId", "name", "gameBranches", "templateSets" },
                [typeof(global::PingCore.Editor.Workspace.Api.Wire.GameBranchView)] = new[] { "gameBranchId", "branchName", "branchDescription", "platform", "defaultBranch", "dataSourceType", "cdnSourceId" },
                [typeof(global::PingCore.Editor.Workspace.Api.Wire.GameTemplateSetView)] = new[] { "templateSetId", "setName" },
                [typeof(global::PingCore.Editor.Workspace.Api.Wire.PushInfoResponse)] = new[] { "source" },
                [typeof(global::PingCore.Editor.Workspace.Api.Wire.PushInfoSourceView)] = new[] { "sourceId", "name" },
                [typeof(global::PingCore.Editor.Workspace.Api.Wire.DeploymentReadResponse)] = new[] { "deployment" },
                [typeof(global::PingCore.Editor.Workspace.Api.Wire.DeploymentReadView)] = new[] { "brandDeploymentId", "deploymentSpecId", "friendlyName" },
            };
            foreach (KeyValuePair<System.Type, string[]> dto in modelled)
            {
                IEnumerable<string> names = dto.Key.GetProperties().SelectMany(p => p.GetCustomAttributes(typeof(Newtonsoft.Json.JsonPropertyAttribute), false).Cast<Newtonsoft.Json.JsonPropertyAttribute>()).Select(a => a.PropertyName);
                Assert.That(names, Is.EqualTo(dto.Value), dto.Key.Name + " models exactly the keys the plugin reads [mutation: model one more key]");
            }
        }

        [Test]
        public void ThePluginCreatesNothingButThePushTokenAndNeverEditsAFleet()
        {
            // Connect, ship and report: every plugin row is a read, except the release calls and the one push token issue.
            WorkspaceRouteId[] writes = { WorkspaceRouteId.CreateRelease, WorkspaceRouteId.CancelRelease, WorkspaceRouteId.AcknowledgeRelease, WorkspaceRouteId.IssuePushToken };
            Assert.That(WorkspaceRoutes.All.Where(r => r.Caller == WorkspaceRouteCaller.Plugin && r.Method != "GET").Select(r => r.Id), Is.EquivalentTo(writes));
            Assert.That(WorkspaceRoutes.All.Where(r => r.Template.StartsWith("registry", System.StringComparison.Ordinal) || r.Template == "images" || r.Template == "locations"), Is.Empty,
                "no image path and no setup wizard");
        }

        [Test]
        public void PathForFillsThePlaceholdersInOrderAndRefusesMissingOrNonPositiveIds()
        {
            WorkspaceRoute get = WorkspaceRoutes.Get(WorkspaceRouteId.GetRelease);
            Assert.That(get.PathFor(1, 12), Is.EqualTo("fleets/1/releases/12"));
            Assert.That(WorkspaceRoutes.Get(WorkspaceRouteId.ListFleets).PathFor(), Is.EqualTo("fleets"));
            Assert.Throws<System.ArgumentException>(() => get.PathFor(1));
            Assert.Throws<System.ArgumentException>(() => get.PathFor(1, 0));
            Assert.Throws<System.ArgumentException>(() => get.PathFor(1, 12, 9));
        }
    }
}
