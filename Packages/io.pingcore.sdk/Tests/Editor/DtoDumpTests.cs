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
using UnityEngine;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// Writes the DTO dump a contract checker reads, and pins the DTO
    /// conventions the dump depends on (explicit JSON names, <c>[Preserve]</c>, no extension data).
    /// </summary>
    public sealed class DtoDumpTests
    {
        private static readonly string[] AllowedKinds = { "string", "integer", "number", "boolean", "object", "array", "map", "any" };

        /// <summary><c>&lt;project&gt;/Library/pingcore-dto-dump.json</c>.</summary>
        private static string DumpPath => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "pingcore-dto-dump.json");

        [Test]
        public void WritesTheDumpToTheProjectLibraryInFormatV1()
        {
            JObject dump = DtoDump.Build();
            Directory.CreateDirectory(Path.GetDirectoryName(DumpPath));
            File.WriteAllText(DumpPath, dump.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));

            JObject reread = JObject.Parse(File.ReadAllText(DumpPath));
            Assert.That((string)reread["format"], Is.EqualTo("pingcore-dto-dump/1"));
            Assert.That(reread.Properties().Select(p => p.Name), Is.EqualTo(new[] { "format", "types", "enums" }));

            var types = (JArray)reread["types"];
            Assert.That(types.Count, Is.GreaterThan(0));
            foreach (JObject type in types.Cast<JObject>())
            {
                Assert.That(type.Properties().Select(p => p.Name), Is.EqualTo(new[] { "clrType", "assembly", "contracts", "properties" }), (string)type["clrType"]);
                foreach (JObject contract in ((JArray)type["contracts"]).Cast<JObject>())
                {
                    Assert.That(contract.Properties().Select(p => p.Name), Is.EqualTo(new[] { "source", "method", "path", "direction", "status" }));
                    Assert.That(new[] { "discovery", "supervisor" }, Does.Contain((string)contract["source"]));
                    Assert.That(new[] { "request", "response", "error" }, Does.Contain((string)contract["direction"]));
                    Assert.That((string)contract["path"], Does.StartWith("/"));
                    Assert.That((string)contract["method"], Is.EqualTo(((string)contract["method"]).ToUpperInvariant()));
                }

                foreach (JObject property in ((JArray)type["properties"]).Cast<JObject>())
                {
                    Assert.That(property.Properties().Select(p => p.Name), Is.EqualTo(new[] { "json", "clr", "type", "ref", "items", "nullable", "required" }));
                    Assert.That(AllowedKinds, Does.Contain((string)property["type"]), $"{type["clrType"]}.{property["clr"]}");
                    string reference = (string)property["ref"];
                    if (reference != null)
                    {
                        Assert.That(types.Any(t => (string)t["clrType"] == reference), Is.True, $"ref {reference} is not itself in the dump");
                    }

                    if (property["items"].Type == JTokenType.Object)
                    {
                        string itemRef = (string)property["items"]["ref"];
                        if (itemRef != null)
                        {
                            Assert.That(types.Any(t => (string)t["clrType"] == itemRef), Is.True, $"items.ref {itemRef} is not itself in the dump");
                        }
                    }
                }
            }

            JObject reasons = ((JArray)reread["enums"]).Cast<JObject>().Single(e => (string)e["role"] == "discovery-reason");
            Assert.That((string)reasons["clrType"], Is.EqualTo("PingCore.Core.DiscoveryReason"));
            Assert.That(((JArray)reasons["values"]).Count, Is.EqualTo(Enum.GetValues(typeof(DiscoveryReason)).Length - 1), "every member but Unknown");
        }

        [Test]
        public void TheDumpListsEveryContractTypeAndPinsTheTicketResponseContracts()
        {
            JObject dump = DtoDump.Build();
            var names = ((JArray)dump["types"]).Select(t => (string)t["clrType"]).ToList();
            foreach (Type type in WireCatalog.ContractTypes())
            {
                Assert.That(names, Does.Contain(type.FullName));
            }

            JObject ticket = ((JArray)dump["types"]).Cast<JObject>().Single(t => (string)t["clrType"] == "PingCore.Discovery.Client.Wire.TicketResponse");
            var tuples = ((JArray)ticket["contracts"]).Select(c => $"{c["source"]} {c["method"]} {c["path"]} {c["direction"]} {c["status"]}").ToList();
            Assert.That(tuples, Is.EquivalentTo(new[]
            {
                "discovery POST /v1/apps/{publicId}/tickets response 200",
                "discovery GET /v1/apps/{publicId}/tickets/{ticketId} response 200",
            }));

            JObject ticketId = ((JArray)ticket["properties"]).Cast<JObject>().Single(p => (string)p["json"] == "ticketId");
            Assert.That((string)ticketId["clr"], Is.EqualTo("TicketId"));
            Assert.That((bool)ticketId["required"], Is.True);
            Assert.That((bool)ticketId["nullable"], Is.False);

            JObject ownerPlayerId = ((JArray)ticket["properties"]).Cast<JObject>().Single(p => (string)p["json"] == "ownerPlayerId");
            Assert.That((bool)ownerPlayerId["required"], Is.True);
            Assert.That((bool)ownerPlayerId["nullable"], Is.True);

            JObject error = ((JArray)ticket["properties"]).Cast<JObject>().First();
            Assert.That((string)error["json"], Is.EqualTo("error"), "base-class properties come first");
        }

        [Test]
        public void TheDumpMapsNestedCollectionsToTheirItemTypes()
        {
            JObject dump = DtoDump.Build();
            JObject list = ((JArray)dump["types"]).Cast<JObject>().Single(t => (string)t["clrType"] == "PingCore.Discovery.Client.Wire.ServerListResponse");
            JObject servers = ((JArray)list["properties"]).Cast<JObject>().Single(p => (string)p["json"] == "servers");
            Assert.That((string)servers["type"], Is.EqualTo("array"));
            Assert.That((string)servers["items"]["type"], Is.EqualTo("object"));
            Assert.That((string)servers["items"]["ref"], Is.EqualTo("PingCore.Discovery.Client.Wire.PublicServer"));

            JObject status = ((JArray)dump["types"]).Cast<JObject>().Single(t => (string)t["clrType"] == "PingCore.Fleet.Wire.GameServerStatus");
            JObject counters = ((JArray)status["properties"]).Cast<JObject>().Single(p => (string)p["json"] == "counters");
            Assert.That((string)counters["type"], Is.EqualTo("map"));
            Assert.That((string)counters["items"]["ref"], Is.EqualTo("PingCore.Fleet.Wire.CounterView"));
        }

        [Test]
        public void EveryWireTypeIsPreservedNamesEveryJsonKeyAndHasNoExtensionData()
        {
            var problems = new List<string>();
            foreach (Type type in WireCatalog.AllWireTypes())
            {
                if (!type.GetCustomAttributes(false).Any(a => a.GetType().Name == "PreserveAttribute"))
                {
                    problems.Add($"{type.FullName}: missing [Preserve]");
                }

                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (MemberInfo member in type.GetMembers(all))
                {
                    if (member.GetCustomAttribute<JsonExtensionDataAttribute>() != null)
                    {
                        problems.Add($"{type.FullName}.{member.Name}: [JsonExtensionData] is banned");
                    }
                }

                var jsonNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (PropertyInfo property in WireCatalog.JsonProperties(type))
                {
                    JsonPropertyAttribute json = WireCatalog.JsonAttribute(property);
                    if (json == null || string.IsNullOrEmpty(json.PropertyName))
                    {
                        problems.Add($"{type.FullName}.{property.Name}: serialized without an explicit [JsonProperty(\"name\")]");
                        continue;
                    }

                    if (!jsonNames.Add(json.PropertyName))
                    {
                        problems.Add($"{type.FullName}: duplicate JSON key {json.PropertyName}");
                    }

                    Type underlying = Nullable.GetUnderlyingType(property.PropertyType);
                    NullValueHandling? nullHandling = WireCatalog.ExplicitNullValueHandling(property);
                    bool explicitNull = nullHandling == NullValueHandling.Include;
                    if (json.Required == Required.Default && nullHandling == null)
                    {
                        problems.Add($"{type.FullName}.{property.Name}: an optional property must say NullValueHandling.Ignore or Include");
                    }

                    if (json.Required == Required.Default && property.PropertyType.IsValueType && underlying == null)
                    {
                        problems.Add($"{type.FullName}.{property.Name}: an optional value type must be Nullable<T>, or absence cannot round-trip");
                    }

                    if (json.Required == Required.Default && explicitNull && type.GetProperty(property.Name + "Specified") == null)
                    {
                        problems.Add($"{type.FullName}.{property.Name}: optional and nullable needs a {property.Name}Specified flag, or absent and null cannot both round-trip");
                    }
                }
            }

            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void DiscoveryReasonsParseEveryWireValueAndMapUnknownValuesToUnknown()
        {
            IReadOnlyList<string> values = DiscoveryReasons.AllWireValues();
            Assert.That(values.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(values.Count), "wire values are unique");
            foreach (string value in values)
            {
                DiscoveryReason parsed = DiscoveryReasons.Parse(value);
                Assert.That(parsed, Is.Not.EqualTo(DiscoveryReason.Unknown), value);
                Assert.That(DiscoveryReasons.ToWireValue(parsed), Is.EqualTo(value));
            }

            Assert.That(DiscoveryReasons.Parse("no_seats"), Is.EqualTo(DiscoveryReason.NoSeats));
            Assert.That(DiscoveryReasons.Parse("wrong_server"), Is.EqualTo(DiscoveryReason.WrongServer));
            Assert.That(DiscoveryReasons.Parse("a_reason_from_the_future"), Is.EqualTo(DiscoveryReason.Unknown));
            Assert.That(DiscoveryReasons.Parse("NO_SEATS"), Is.EqualTo(DiscoveryReason.Unknown), "matching is exact");
            Assert.That(DiscoveryReasons.Parse(null), Is.EqualTo(DiscoveryReason.Unknown));
            Assert.That(DiscoveryReasons.Parse(string.Empty), Is.EqualTo(DiscoveryReason.Unknown));
            Assert.That(DiscoveryReasons.ToWireValue(DiscoveryReason.Unknown), Is.Null);
        }

        [Test]
        public void TheSdkVersionConstantMatchesThePackageManifest()
        {
            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(RepoPaths.PackageRoot, "package.json")));
            Assert.That(PingCoreSdkInfo.Version, Is.EqualTo((string)manifest["version"]));
        }
    }
}
