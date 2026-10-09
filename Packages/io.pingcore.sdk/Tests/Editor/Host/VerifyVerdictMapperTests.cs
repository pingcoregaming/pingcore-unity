using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Discovery.Host.Tests.Editor
{
    public sealed class VerifyVerdictMapperTests
    {
        private const string Own = "203.0.113.10:27015";
        private const string Player = "player-1";

        private static VerifyResult MapOk(string body)
        {
            DiscoveryResult<VerifyReservationResponse> call = DiscoveryCaller.Classify<VerifyReservationResponse>(new PingCoreHttpResponse(200, new Dictionary<string, string>(), body));
            Assert.That(call.IsOk, Is.True, "the canned body must parse: " + body);
            return VerifyVerdictMapper.Map(call, call.Value, Own, Player);
        }

        private static string Detailed(bool valid, string serverId, string playerIds)
        {
            return "{\"error\":false,\"valid\":" + (valid ? "true" : "false") + ",\"reservationId\":\"rsv-1\",\"serverId\":\"" + serverId + "\",\"seats\":2,\"playerIds\":" + playerIds
                + ",\"context\":null,\"expiresAt\":1790640000000,\"ownerKind\":\"backend\",\"ownerPlayerId\":null}";
        }

        [Test]
        public void TheVerdictTableMapsEveryAnswerForm()
        {
            var rows = new List<(string body, VerifyVerdict verdict, bool detailed, string why)>
            {
                ("{\"error\":false,\"valid\":true}", VerifyVerdict.Valid, false, "verdict only, valid (shipped token: Discovery admitted the seat)"),
                ("{\"error\":false,\"valid\":false}", VerifyVerdict.Invalid, false, "verdict only, not valid"),
                (Detailed(true, Own, "null"), VerifyVerdict.Valid, true, "detailed, own game server, unnamed seats"),
                (Detailed(true, Own, "[\"player-2\",\"player-1\"]"), VerifyVerdict.Valid, true, "detailed, own game server, player named"),
                (Detailed(true, Own, "[\"player-2\"]"), VerifyVerdict.NotInReservation, true, "detailed valid but the player is not named"),
                (Detailed(true, "198.51.100.7:27015", "null"), VerifyVerdict.WrongServer, true, "detailed valid for another game server"),
                ("{\"error\":false,\"valid\":true,\"reservationId\":\"rsv-1\"}", VerifyVerdict.WrongServer, true, "detailed valid with no serverId cannot be ours"),
                ("{\"error\":false,\"valid\":false,\"reason\":\"wrong_server\"}", VerifyVerdict.WrongServer, true, "reason wrong_server"),
                ("{\"error\":false,\"valid\":false,\"reason\":\"not_in_reservation\"}", VerifyVerdict.NotInReservation, true, "reason not_in_reservation"),
                ("{\"error\":false,\"valid\":false,\"reason\":\"a_future_reason\"}", VerifyVerdict.Invalid, true, "an unknown reason is a plain refusal"),
            };
            foreach (var row in rows)
            {
                VerifyResult result = MapOk(row.body);
                Assert.That(result.Verdict, Is.EqualTo(row.verdict), row.why);
                Assert.That(result.Detailed, Is.EqualTo(row.detailed), row.why);
                Assert.That(result.IsValid, Is.EqualTo(row.verdict == VerifyVerdict.Valid), row.why);
            }
        }

        [Test]
        public void AnythingButAnAcceptedAnswerIsUnavailableEvenWhenABodySaysValid()
        {
            // Each row carries a parsed valid:true body, so a mapper that looked only at the body would say Valid.
            var validBody = JsonConvert.DeserializeObject<VerifyReservationResponse>("{\"error\":false,\"valid\":true}", PingCoreJson.Settings);
            var outcomes = new List<(DiscoveryOutcome outcome, int status)>
            {
                (DiscoveryOutcome.RateLimited, 429),
                (DiscoveryOutcome.Degraded, 503),
                (DiscoveryOutcome.Unexpected, 500),
                (DiscoveryOutcome.Unauthorized, 401),
                (DiscoveryOutcome.Forbidden, 403),
                (DiscoveryOutcome.NotFound, 404),
                (DiscoveryOutcome.Unreachable, 0),
                (DiscoveryOutcome.Cancelled, 0),
                (DiscoveryOutcome.InvalidRequest, 0),
            };
            foreach (var row in outcomes)
            {
                var call = new DiscoveryResult<VerifyReservationResponse>(row.outcome, row.status, null, "x", null, TimeSpan.FromSeconds(1), null, validBody);
                VerifyResult result = VerifyVerdictMapper.Map(call, call.Value, Own, Player);
                Assert.That(result.Verdict, Is.EqualTo(VerifyVerdict.Unavailable), row.outcome.ToString());
                Assert.That(result.IsValid, Is.False, row.outcome.ToString());
            }

            // Control: the same body on an accepted answer is Valid, so the rows above can fail.
            var ok = new DiscoveryResult<VerifyReservationResponse>(DiscoveryOutcome.Ok, 200, null, null, null, null, null, validBody);
            Assert.That(VerifyVerdictMapper.Map(ok, ok.Value, Own, Player).Verdict, Is.EqualTo(VerifyVerdict.Valid));
            Assert.That(VerifyVerdictMapper.Map(null, null, Own, Player).Verdict, Is.EqualTo(VerifyVerdict.Unavailable));
        }

        [Test]
        public void TheShippedVerdictOnlyFixtureIsNotDetailedAndTheDetailedOneIs()
        {
            Assert.That(VerifyVerdictMapper.IsDetailed(JsonConvert.DeserializeObject<VerifyReservationResponse>("{\"error\":false,\"valid\":true}", PingCoreJson.Settings)), Is.False);
            Assert.That(VerifyVerdictMapper.IsDetailed(JsonConvert.DeserializeObject<VerifyReservationResponse>("{\"error\":false,\"valid\":true,\"playerIds\":null}", PingCoreJson.Settings)), Is.True,
                "an explicit null playerIds is a detail field");
            Assert.That(VerifyVerdictMapper.IsDetailed(JsonConvert.DeserializeObject<VerifyReservationResponse>("{\"error\":false,\"valid\":false,\"reason\":\"wrong_server\"}", PingCoreJson.Settings)), Is.True);
        }
    }
}
