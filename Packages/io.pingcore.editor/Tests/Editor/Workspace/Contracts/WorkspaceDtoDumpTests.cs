using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using UnityEngine;

namespace PingCore.Editor.Workspace.Tests.Contracts
{
    /// <summary>
    /// Writes <c>&lt;project&gt;/Library/pingcore-editor-dto-dump.json</c>, which a contract checker
    /// reads beside the SDK dump, and pins the conventions the dump and the fixture round trip depend on.
    /// </summary>
    public sealed class WorkspaceDtoDumpTests
    {
        private static string DumpPath => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "pingcore-editor-dto-dump.json");

        [Test]
        public void WritesTheEditorDumpToTheProjectLibrary()
        {
            JObject dump = WorkspaceWireCatalog.BuildDump();
            Directory.CreateDirectory(Path.GetDirectoryName(DumpPath));
            File.WriteAllText(DumpPath, dump.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));

            JObject reread = JObject.Parse(File.ReadAllText(DumpPath));
            Assert.That(reread.Properties().Select(p => p.Name), Is.EqualTo(new[] { "format", "types", "enums", "routes" }));
            var routes = (JArray)reread["routes"];
            Assert.That(routes.Select(r => (string)r["id"]), Is.EqualTo(WorkspaceRoutes.All.Select(r => r.Id.ToString())), "every row of the route table, in order");
            foreach (JObject route in routes.Cast<JObject>())
            {
                Assert.That(route.Properties().Select(p => p.Name), Is.EqualTo(new[] { "id", "method", "template", "step", "caller" }), "method and path only: no route file or pattern");
            }

            JObject game = routes.Cast<JObject>().Single(r => (string)r["id"] == nameof(WorkspaceRouteId.GetGame));
            Assert.That(((string)game["method"], (string)game["template"], (string)game["step"], (string)game["caller"]), Is.EqualTo(("GET", "my-games/{id}", "ship", "Plugin")));
            Assert.That((string)reread["format"], Is.EqualTo("pingcore-dto-dump/1"));
            var types = (JArray)reread["types"];
            Assert.That(types.Count, Is.GreaterThan(20));
            foreach (JObject type in types.Cast<JObject>())
            {
                Assert.That((string)type["assembly"], Is.EqualTo("PingCore.Editor.Workspace"));
                foreach (JObject contract in ((JArray)type["contracts"]).Cast<JObject>())
                {
                    Assert.That((string)contract["source"], Is.EqualTo("pingcore-api"), (string)type["clrType"]);
                    Assert.That((string)contract["path"], Does.StartWith("/"));
                }

                foreach (JObject property in ((JArray)type["properties"]).Cast<JObject>())
                {
                    string reference = (string)property["ref"];
                    if (reference != null)
                    {
                        Assert.That(types.Any(t => (string)t["clrType"] == reference), Is.True, $"ref {reference} is not in the dump");
                    }
                }
            }
        }

        [Test]
        public void TheDumpPinsTheFleetListContractAndTheNullableFields()
        {
            JObject dump = WorkspaceWireCatalog.BuildDump();
            JObject list = Type(dump, typeof(FleetListResponse));
            Assert.That(((JArray)list["contracts"]).Select(c => $"{c["source"]} {c["method"]} {c["path"]} {c["direction"]} {c["status"]}"),
                Is.EqualTo(new[] { "pingcore-api GET /fleets response 200" }));
            JObject fleets = Property(list, "fleets");
            Assert.That((string)fleets["type"], Is.EqualTo("array"));
            Assert.That((string)fleets["items"]["ref"], Is.EqualTo(typeof(FleetView).FullName));

            JObject fleet = Type(dump, typeof(FleetView));
            Assert.That((bool)Property(fleet, "gameName")["nullable"], Is.True);
            Assert.That((bool)Property(fleet, "name")["nullable"], Is.False);
            Assert.That((string)Property(fleet, "agentConfig")["type"], Is.EqualTo("object"));

            JObject status = Type(dump, typeof(ReleaseStatusView));
            Assert.That(((JArray)status["properties"]).First()["json"].ToString(), Is.EqualTo("releaseId"), "base-class properties come first");
            Assert.That(((JArray)status["properties"]).Last()["json"].ToString(), Is.EqualTo("blocked"));
        }

        [Test]
        public void EveryEditorWireTypeIsPreservedNamesEveryKeyAndSaysItsNullHandling()
        {
            var problems = new List<string>();
            foreach (Type type in WorkspaceWireCatalog.AllWireTypes())
            {
                if (type.GetCustomAttribute<PreserveAttribute>(false) == null)
                {
                    problems.Add($"{type.FullName}: missing [Preserve]");
                }

                foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (member.GetCustomAttribute<JsonExtensionDataAttribute>() != null)
                    {
                        problems.Add($"{type.FullName}.{member.Name}: [JsonExtensionData] is banned");
                    }
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (PropertyInfo property in WorkspaceWireCatalog.JsonProperties(type))
                {
                    JsonPropertyAttribute json = property.GetCustomAttribute<JsonPropertyAttribute>();
                    if (json == null || string.IsNullOrEmpty(json.PropertyName))
                    {
                        problems.Add($"{type.FullName}.{property.Name}: no explicit [JsonProperty(\"name\")]");
                        continue;
                    }

                    if (!names.Add(json.PropertyName))
                    {
                        problems.Add($"{type.FullName}: duplicate key {json.PropertyName}");
                    }

                    if (json.Required == Required.Default && WorkspaceWireCatalog.ExplicitNullValueHandling(property) == null)
                    {
                        problems.Add($"{type.FullName}.{property.Name}: must say NullValueHandling.Ignore or Include");
                    }
                }
            }

            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void EveryContractNamesARouteOfTheTableWithItsMethodAndTemplate()
        {
            var problems = new List<string>();
            foreach (Type type in WorkspaceWireCatalog.ContractTypes())
            {
                foreach (WireContractAttribute contract in type.GetCustomAttributes<WireContractAttribute>(false))
                {
                    if (contract.Source != PingCoreApiContract.Source)
                    {
                        problems.Add($"{type.FullName}: source {contract.Source}");
                    }

                    // A route the plugin sends, with its key or (push info only) with a pasted push token; never a pingctl-only route.
                    bool known = WorkspaceRoutes.All.Any(r => r.Caller != WorkspaceRouteCaller.Pingctl && r.Method == contract.Method && "/" + r.Template == contract.Path);
                    if (!known)
                    {
                        problems.Add($"{type.FullName}: {contract.Method} {contract.Path} is not a plugin route of WorkspaceRoutes");
                    }
                }
            }

            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        private static JObject Type(JObject dump, Type type) => ((JArray)dump["types"]).Cast<JObject>().Single(t => (string)t["clrType"] == type.FullName);

        private static JObject Property(JObject type, string json) => ((JArray)type["properties"]).Cast<JObject>().Single(p => (string)p["json"] == json);
    }
}
