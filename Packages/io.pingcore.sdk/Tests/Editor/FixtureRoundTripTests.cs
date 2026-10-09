using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// Replays every contract fixture (<c>contracts/**/fixtures/*.json</c>, format
    /// <c>pingcore-fixture/1</c>) through the DTO it names. Unknown members are errors, the
    /// re-serialized body must deep-equal the payload, and the payload's top-level keys must
    /// equal the fixture's pinned <c>keys</c>, so a silent rename fails here. Set
    /// <c>PINGCORE_FIXTURES_DIR</c> to replay a different fixture root.
    /// Failure messages name JSON paths, never values, because a payload can hold a token.
    /// </summary>
    public sealed class FixtureRoundTripTests
    {
        private const string MissingRootCase = "<fixture root missing>";
        private const string EmptyRootCase = "<no fixtures found>";

        public static IEnumerable<TestCaseData> Fixtures()
        {
            string root = RepoPaths.FixtureSearchRoot(out string problem);
            if (root == null)
            {
                yield return new TestCaseData(MissingRootCase, problem).SetName("Fixture root is present");
                yield break;
            }

            List<string> files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
                .Where(RepoPaths.IsSdkFixture)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
            {
                yield return new TestCaseData(EmptyRootCase, $"no fixtures/*.json under {root}").SetName("Fixture set is not empty");
                yield break;
            }

            foreach (string file in files)
            {
                string relative = RepoPaths.Relative(root, file);
                yield return new TestCaseData(file, relative).SetName($"Fixture round-trips: {relative}");
            }
        }

        [TestCaseSource(nameof(Fixtures))]
        public void FixturePayloadRoundTripsThroughItsDto(string fixturePath, string label)
        {
            if (fixturePath == MissingRootCase || fixturePath == EmptyRootCase)
            {
                Assert.Fail(label);
            }

            JObject fixture = ParseStrict(File.ReadAllText(fixturePath));

            Assert.That((string)fixture["fixture"], Is.EqualTo("pingcore-fixture/1"), $"{label}: fixture format");
            Assert.That((string)fixture["source"], Does.Match("^(Discovery|supervisor) [0-9]+\\.[0-9]+\\.[0-9]+ "), $"{label}: source names the service and its version");

            string dtoName = (string)fixture["dto"];
            Type dto = WireCatalog.FindType(dtoName ?? string.Empty);
            Assert.That(dto, Is.Not.Null, $"{label}: dto {dtoName} is not a type in the SDK runtime assemblies");

            AssertContractDeclared(dto, fixture["contract"] as JObject, label);

            Assert.That(fixture["payload"], Is.InstanceOf<JObject>(), $"{label}: payload must be a JSON object");
            var payload = (JObject)fixture["payload"];
            Assert.That(fixture["keys"], Is.InstanceOf<JArray>(), $"{label}: keys must be an array");
            List<string> pinned = ((JArray)fixture["keys"]).Select(k => (string)k).OrderBy(k => k, StringComparer.Ordinal).ToList();
            List<string> actual = payload.Properties().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.That(actual, Is.EqualTo(pinned), $"{label}: payload top-level keys differ from the pinned keys");

            JsonSerializer serializer = JsonSerializer.Create(PingCoreJson.CreateStrictSettings());
            object model;
            try
            {
                model = payload.ToObject(dto, serializer);
            }
            catch (JsonException e)
            {
                Assert.Fail($"{label}: payload does not deserialize into {dto.FullName} with unknown members as errors: {Redact(e)}");
                return;
            }

            JToken back = JToken.FromObject(model, serializer);
            List<string> differences = Differences(payload, back, "$");
            Assert.That(differences, Is.Empty, $"{label}: re-serialized {dto.FullName} differs from the payload at:\n" + string.Join("\n", differences));
        }

        [Test]
        public void TheDiffReportsARenamedKeyWithoutPrintingValues()
        {
            JObject original = JObject.Parse("{\"ticketId\":\"secret-value\",\"n\":1}");
            JObject renamed = JObject.Parse("{\"ticketID\":\"secret-value\",\"n\":1}");
            List<string> differences = Differences(original, renamed, "$");
            Assert.That(differences, Is.EquivalentTo(new[] { "$.ticketId: missing after round trip", "$.ticketID: added by round trip" }));
            Assert.That(string.Join("\n", differences), Does.Not.Contain("secret-value"));
        }

        [Test]
        public void ADeserializationErrorIsReportedWithoutTheOffendingValue()
        {
            JObject payload = JObject.Parse("{\"error\":false,\"ticketId\":\"t\",\"cancelled\":\"not-a-boolean-secret\"}");
            JsonSerializer serializer = JsonSerializer.Create(PingCoreJson.CreateStrictSettings());
            JsonException caught = null;
            try
            {
                payload.ToObject<PingCore.Discovery.Client.Wire.CancelTicketResponse>(serializer);
            }
            catch (JsonException e)
            {
                caught = e;
            }

            Assert.That(caught, Is.Not.Null, "precondition: a string where a boolean belongs must fail");
            Assert.That(caught.Message, Does.Contain("not-a-boolean-secret"), "precondition: Newtonsoft quotes the value");
            string redacted = Redact(caught);
            Assert.That(redacted, Does.Not.Contain("not-a-boolean-secret"));
            Assert.That(redacted, Does.Contain("cancelled"), "the path survives");
        }

        [Test]
        public void AFixtureKeyTheDtoDoesNotModelFailsTheStrictRoundTrip()
        {
            JObject payload = JObject.Parse("{\"error\":false,\"ticketId\":\"t\",\"cancelled\":true,\"unmodelled\":1}");
            JsonSerializer serializer = JsonSerializer.Create(PingCoreJson.CreateStrictSettings());
            Assert.Throws<JsonSerializationException>(() => payload.ToObject<PingCore.Discovery.Client.Wire.CancelTicketResponse>(serializer));

            JsonSerializer runtime = JsonSerializer.Create(PingCoreJson.Settings);
            Assert.DoesNotThrow(() => payload.ToObject<PingCore.Discovery.Client.Wire.CancelTicketResponse>(runtime), "runtime settings ignore additive fields");
        }

        [Test]
        public void AnOptionalNullableFieldKeepsAbsentAndNullApart()
        {
            JsonSerializer serializer = JsonSerializer.Create(PingCoreJson.CreateStrictSettings());
            foreach (string body in new[]
            {
                "{\"error\":false,\"valid\":true}",
                "{\"error\":false,\"valid\":true,\"context\":null,\"playerIds\":null,\"ownerPlayerId\":null}",
                "{\"error\":false,\"valid\":true,\"context\":{\"when\":\"2026-10-02T00:00:00Z\"},\"playerIds\":[\"p\"],\"ownerPlayerId\":\"p\"}",
            })
            {
                JObject payload = ParseStrict(body);
                object model = payload.ToObject(typeof(PingCore.Discovery.Host.Wire.VerifyReservationResponse), serializer);
                JToken back = JToken.FromObject(model, serializer);
                Assert.That(Differences(payload, back, "$"), Is.Empty, body);
            }
        }

        private static void AssertContractDeclared(Type dto, JObject contract, string label)
        {
            Assert.That(contract, Is.Not.Null, $"{label}: contract must be an object");
            bool declared = dto.GetCustomAttributes<WireContractAttribute>(false).Any(c => ContractMatches(c, contract));
            Assert.That(declared, Is.True, $"{label}: {dto.FullName} carries no [WireContract({contract["source"]}, {contract["method"]}, {contract["path"]}, {contract["direction"]}, {contract["status"]})]");
        }

        /// <summary>
        /// True when a fixture's <c>contract</c> object names exactly this attribute's route:
        /// same source, method, path and direction, and the same status (a request has none, or 0).
        /// </summary>
        internal static bool ContractMatches(WireContractAttribute attribute, JObject contract)
        {
            if (attribute == null || contract == null)
            {
                return false;
            }

            JToken statusToken = contract["status"];
            int? status = statusToken == null || statusToken.Type == JTokenType.Null ? (int?)null
                : statusToken.Type == JTokenType.Integer ? (int)statusToken : -1;
            return attribute.Source == (string)contract["source"]
                && attribute.Method == (string)contract["method"]
                && attribute.Path == (string)contract["path"]
                && DtoDump.DirectionName(attribute.Direction) == (string)contract["direction"]
                && (attribute.Direction == WireDirection.Request ? status == null || status == 0 : status == attribute.Status);
        }

        /// <summary>Parses a fixture file the way the round trip does (no date or float coercion).</summary>
        internal static JObject ParseFixture(string json) => ParseStrict(json);

        private static JObject ParseStrict(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                return JObject.Load(reader);
            }
        }

        /// <summary>JSON paths where <paramref name="expected"/> and <paramref name="actual"/> differ. Values are never included.</summary>
        internal static List<string> Differences(JToken expected, JToken actual, string path)
        {
            var differences = new List<string>();
            if (expected.Type != actual.Type)
            {
                differences.Add($"{path}: {expected.Type} became {actual.Type}");
                return differences;
            }

            switch (expected)
            {
                case JObject expectedObject:
                    var actualObject = (JObject)actual;
                    foreach (JProperty property in expectedObject.Properties())
                    {
                        JToken other = actualObject[property.Name];
                        string childPath = $"{path}.{property.Name}";
                        if (other == null && !actualObject.ContainsKey(property.Name))
                        {
                            differences.Add($"{childPath}: missing after round trip");
                        }
                        else
                        {
                            differences.AddRange(Differences(property.Value, other, childPath));
                        }
                    }

                    foreach (JProperty property in actualObject.Properties())
                    {
                        if (!expectedObject.ContainsKey(property.Name))
                        {
                            differences.Add($"{path}.{property.Name}: added by round trip");
                        }
                    }

                    break;
                case JArray expectedArray:
                    var actualArray = (JArray)actual;
                    if (expectedArray.Count != actualArray.Count)
                    {
                        differences.Add($"{path}: array length {expectedArray.Count} became {actualArray.Count}");
                        break;
                    }

                    for (int i = 0; i < expectedArray.Count; i++)
                    {
                        differences.AddRange(Differences(expectedArray[i], actualArray[i], $"{path}[{i}]"));
                    }

                    break;
                default:
                    if (!JToken.DeepEquals(expected, actual))
                    {
                        differences.Add($"{path}: value changed");
                    }

                    break;
            }

            return differences;
        }

        /// <summary>
        /// The JSON path and the first clause of a Newtonsoft error. A conversion error quotes the
        /// offending value after a colon ("Could not convert string to integer: ..."), so the
        /// message is cut at the first ": " or ". ", which keeps member names and drops values.
        /// </summary>
        internal static string Redact(JsonException e)
        {
            string message = e.Message ?? string.Empty;
            int colon = message.IndexOf(": ", StringComparison.Ordinal);
            int stop = message.IndexOf(". ", StringComparison.Ordinal);
            int cut = colon >= 0 && (stop < 0 || colon < stop) ? colon : stop;
            string clause = cut >= 0 ? message.Substring(0, cut) : message;
            string path = (e as JsonSerializationException)?.Path ?? (e as JsonReaderException)?.Path ?? "?";
            return $"{e.GetType().Name} at '{path}': {clause}";
        }
    }
}
