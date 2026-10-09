using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// Bodies the local SDK endpoint sends that the snapshot spec gives no JSON schema (the
    /// watch line, the joinable echo, the plain <c>{message}</c> refusals), so they cannot be
    /// <c>contracts/</c> fixtures: a contract checker refuses a fixture for an operation without a
    /// schema. They live in <c>Tests/Editor/Fleet/Bodies/</c>, transcribed from supervisor 1.3.4's
    /// local SDK endpoint, and round-trip strictly here the way the fixtures do.
    /// </summary>
    public sealed class FleetBodyTests
    {
        public static IEnumerable<TestCaseData> Bodies()
        {
            PackageInfo info = PackageInfo.FindForAssembly(typeof(FleetSdk).Assembly);
            string dir = info == null ? null : Path.Combine(info.resolvedPath, "Tests", "Editor", "Fleet", "Bodies");
            if (dir == null || !Directory.Exists(dir))
            {
                yield return new TestCaseData(null).SetName("Fleet bodies directory is present");
                yield break;
            }

            foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                yield return new TestCaseData(file).SetName("Fleet body round-trips: " + Path.GetFileName(file));
            }
        }

        [TestCaseSource(nameof(Bodies))]
        public void EachBodyRoundTripsStrictlyThroughItsDto(string file)
        {
            Assert.That(file, Is.Not.Null, "Tests/Editor/Fleet/Bodies is missing");
            JObject doc = Parse(File.ReadAllText(file));
            Assert.That((string)doc["body"], Is.EqualTo("pingcore-body/1"));
            Assert.That((string)doc["source"], Is.EqualTo("local SDK endpoint behaviour (supervisor 1.3.4)"));
            Type dto = typeof(FleetSdk).Assembly.GetType((string)doc["dto"], false);
            Assert.That(dto, Is.Not.Null, (string)doc["dto"]);
            Assert.That(dto.GetCustomAttributes(false).Any(a => a.GetType().Name == "PreserveAttribute"), Is.True, "[Preserve]");

            var payload = (JObject)doc["payload"];
            JsonSerializer strict = JsonSerializer.Create(PingCoreJson.CreateStrictSettings());
            object model = payload.ToObject(dto, strict);
            JToken back = JToken.FromObject(model, strict);
            Assert.That(JToken.DeepEquals(payload, back), Is.True, "re-serialized body differs from the transcribed payload");
        }

        [Test]
        public void AnUnmodelledKeyInAWatchFrameFailsTheStrictRoundTrip()
        {
            JObject frame = Parse("{\"result\":{\"object_meta\":{\"name\":\"gameserver-1\"},\"status\":{\"state\":\"Ready\",\"players\":3}}}");
            JsonSerializer strict = JsonSerializer.Create(PingCoreJson.CreateStrictSettings());
            Assert.Throws<JsonSerializationException>(() => frame.ToObject<PingCore.Fleet.Wire.WatchFrame>(strict));
            JsonSerializer runtime = JsonSerializer.Create(PingCoreJson.Settings);
            Assert.DoesNotThrow(() => frame.ToObject<PingCore.Fleet.Wire.WatchFrame>(runtime), "the runtime ignores an additive field");
        }

        [Test]
        public void AWatchLineWithoutAResultIsRefused()
        {
            Assert.That(LocalSdkValues.TryDeserialize<PingCore.Fleet.Wire.WatchFrame>("{\"object_meta\":{}}", out string problem), Is.Null);
            Assert.That(problem, Is.Not.Null);
            Assert.That(LocalSdkValues.TryDeserialize<PingCore.Fleet.Wire.WatchFrame>("{\"result\":null}", out _), Is.Null);
            Assert.That(LocalSdkValues.TryDeserialize<PingCore.Fleet.Wire.WatchFrame>("{\"result\":{}}", out _), Is.Not.Null, "an empty view is still a frame");
        }

        [Test]
        public void JoinableAttributesKeepIntegersAsIntegersAndFractionsAsFractions()
        {
            var request = new PingCore.Fleet.Wire.JoinableSessionRequest
            {
                OpenSeats = 3,
                Attributes = new Dictionary<string, double> { ["skill"] = 1210, ["ratio"] = 0.5 },
            };
            JObject json = JObject.Parse(LocalSdkValues.Serialize(request));
            Assert.That(json["attributes"]["skill"].Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(json["attributes"]["ratio"].Type, Is.EqualTo(JTokenType.Float));
            Assert.That(json.Properties().Select(p => p.Name), Is.EqualTo(new[] { "openSeats", "attributes" }), "unset optional fields are left out");
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<PingCore.Fleet.Wire.JoinableSessionRequest>("{\"openSeats\":1,\"attributes\":{\"skill\":\"high\"}}", PingCoreJson.Settings), "a non-number attribute is refused");
        }

        private static JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                return JObject.Load(reader);
            }
        }
    }
}
