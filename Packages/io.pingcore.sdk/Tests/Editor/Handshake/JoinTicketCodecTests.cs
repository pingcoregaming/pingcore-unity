using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PingCore.Core.Discovery;
using PingCore.Core.Handshake;

namespace PingCore.Sdk.Tests.Editor.Handshake
{
    /// <summary>
    /// The v1 join-ticket codec against the contract examples in <c>contracts/handshake/examples/</c> (valid)
    /// and <c>examples/invalid/</c> (must be refused), and against malformed payloads. Every malformed case is
    /// a mutation of <see cref="ValidMatch"/>, which itself must decode, so a case that passes proves the rule
    /// it breaks.
    /// </summary>
    public sealed class JoinTicketCodecTests
    {
        internal const string ValidMatch =
            "{\"v\":1,\"kind\":\"match\",\"protocolVersion\":3,\"playerId\":\"anon:p1\",\"ticketId\":\"t1\",\"allocationId\":\"a1\"}";

        [Test]
        public void EveryValidContractExampleDecodesAsTheKindItsNameStartsWith()
        {
            string[] files = Directory.GetFiles(HandshakeTestData.ExamplesDirectory, "*.json");
            Assert.That(files.Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal),
                Is.EqualTo(new[] { "backfill", "lan", "match", "reservation", "reservation.party" }),
                "a new example needs a row here, so its kind is asserted");

            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                JoinTicketParseResult result = JoinTicketCodec.Decode(File.ReadAllBytes(file));
                Assert.That(result.IsValid, Is.True, name + ": " + result.Error + " " + result.Detail);
                Assert.That(JoinTicketKinds.ToWire(result.Ticket.Kind), Is.EqualTo(name.Split('.')[0]), name);
            }
        }

        [Test]
        public void EveryInvalidContractExampleIsRefusedAndTheOversizeOneAsTooLarge()
        {
            string[] files = Directory.GetFiles(HandshakeTestData.InvalidExamplesDirectory, "*.json");
            Assert.That(files.Select(Path.GetFileNameWithoutExtension), Is.EquivalentTo(new[] { "oversize" }), "a new invalid example needs its expected reason here");
            byte[] oversize = File.ReadAllBytes(Path.Combine(HandshakeTestData.InvalidExamplesDirectory, "oversize.json"));
            Assert.That(oversize.Length, Is.GreaterThan(JoinTicketCodec.MaxPayloadBytes));
            JoinTicketParseResult result = JoinTicketCodec.Decode(oversize);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo(JoinRejectReason.PayloadTooLarge), result.Detail);
        }

        [Test]
        public void TheOversizeExampleBreaksOnlyTheSizeCap()
        {
            // Compact and re-read without the cap: every field is within the schema, so the size is the only fault.
            string text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(HandshakeTestData.InvalidExamplesDirectory, "oversize.json")));
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(text);
            JoinTicket ticket = JoinTicket.ForMatch((string)parsed["ticketId"], (string)parsed["allocationId"], (string)parsed["playerId"], (int)parsed["protocolVersion"]);
            Assert.That(JoinTicketCodec.TryEncode(ticket, out byte[] payload, out string problem), Is.False);
            Assert.That(payload, Is.Null);
            Assert.That(problem, Does.Contain("cap is 1024"));
        }

        [Test]
        public void TheMatchExampleKeepsEveryField()
        {
            JoinTicket ticket = JoinTicketCodec.Decode(HandshakeTestData.Example("match")).Ticket;
            Assert.That(ticket.Version, Is.EqualTo(1));
            Assert.That(ticket.Kind, Is.EqualTo(JoinTicketKind.Match));
            Assert.That(ticket.ProtocolVersion, Is.EqualTo(3));
            Assert.That(ticket.PlayerId, Is.EqualTo("anon:0b84e1c9-437c-4962-ab0a-7ffdd2123607"));
            Assert.That(ticket.TicketId, Is.EqualTo("q3Vx9mKc0TzR1bN4wYp7Lg"));
            Assert.That(ticket.AllocationId, Is.EqualTo("9b2f6c1e-4d7a-4f0e-8a3b-2c5d7e9f1a40"));
            Assert.That(ticket.DisplayName, Is.EqualTo("Bob"));
            Assert.That(ticket.ReservationId, Is.Null);
        }

        [Test]
        public void ThePartyReservationExampleKeepsEveryField()
        {
            JoinTicket ticket = JoinTicketCodec.Decode(HandshakeTestData.Example("reservation.party")).Ticket;
            Assert.That(ticket.Kind, Is.EqualTo(JoinTicketKind.Reservation));
            Assert.That(ticket.PlayerId, Is.EqualTo("anon:7c4e2a90-5b1d-4f6e-9a3c-8d2b1e0f4a67"));
            Assert.That(ticket.ReservationId, Is.EqualTo("c1d9e8f2-6a4b-4c3d-b5e7-0f1a2b3c4d5e"));
            Assert.That(ticket.DisplayName, Is.EqualTo("Dana"));
            Assert.That(ticket.TicketId, Is.Null);
            Assert.That(ticket.TicketRef, Is.Null);
        }

        [Test]
        public void EveryValidExampleSurvivesAnEncodeDecodeRoundTripWithTheSameFields()
        {
            foreach (string file in Directory.GetFiles(HandshakeTestData.ExamplesDirectory, "*.json"))
            {
                JoinTicket first = JoinTicketCodec.Decode(File.ReadAllBytes(file)).Ticket;
                JoinTicket again = JoinTicketCodec.Decode(JoinTicketCodec.Encode(first)).Ticket;
                Assert.That(again, Is.Not.Null, file);
                Assert.That(Fields(again), Is.EqualTo(Fields(first)), file);
            }
        }

        [Test]
        public void TheEncoderWritesCompactJsonInSchemaOrder()
        {
            byte[] payload = JoinTicketCodec.Encode(JoinTicket.ForMatch("t1", "a1", "anon:p1", 3, "Bob"));
            Assert.That(Encoding.UTF8.GetString(payload),
                Is.EqualTo("{\"v\":1,\"kind\":\"match\",\"protocolVersion\":3,\"playerId\":\"anon:p1\",\"ticketId\":\"t1\",\"allocationId\":\"a1\",\"displayName\":\"Bob\"}"));
            Assert.That(Encoding.UTF8.GetString(JoinTicketCodec.Encode(JoinTicket.ForReservation("r1", "anon:p1", 0))),
                Is.EqualTo("{\"v\":1,\"kind\":\"reservation\",\"protocolVersion\":0,\"playerId\":\"anon:p1\",\"reservationId\":\"r1\"}"));
            Assert.That(Encoding.UTF8.GetString(JoinTicketCodec.Encode(JoinTicket.ForLan("lan-1", 7))),
                Is.EqualTo("{\"v\":1,\"kind\":\"lan\",\"protocolVersion\":7,\"playerId\":\"lan-1\"}"));
        }

        [Test]
        public void TheEncoderRefusesAPayloadOverTheCapAndALoneSurrogate()
        {
            string wide = string.Concat(Enumerable.Repeat("\U0001D538", 100));
            JoinTicket huge = JoinTicket.ForBackfill(wide, wide, wide, 3);
            Assert.Throws<ArgumentException>(() => JoinTicketCodec.Encode(huge));
            JoinTicket broken = JoinTicket.ForLan("lan-1", 3, "bad\uD800name");
            Assert.That(JoinTicketCodec.TryEncode(broken, out _, out string problem), Is.False);
            Assert.That(problem, Does.Contain("surrogate"));
        }

        [Test]
        public void TheFactoriesRefuseFieldsTheSchemaForbidsWithoutQuotingThem()
        {
            const string secret = "secret-looking-value";
            var cases = new TestDelegate[]
            {
                () => JoinTicket.ForMatch(string.Empty, "a1", "p1", 3),
                () => JoinTicket.ForMatch(secret + new string('x', 100), "a1", "p1", 3),
                () => JoinTicket.ForMatch("t1", null, "p1", 3),
                () => JoinTicket.ForBackfill("t1", "a1", string.Empty, 3),
                () => JoinTicket.ForReservation(null, "p1", 3),
                () => JoinTicket.ForReservation("r1", "p1", -1),
                () => JoinTicket.ForLan("p1", 3, new string('d', 33)),
            };
            foreach (TestDelegate attempt in cases)
            {
                var error = Assert.Throws<ArgumentException>(attempt);
                Assert.That(error.Message, Does.Not.Contain(secret));
            }
        }

        [Test]
        public void TheTicketRefIsTwelveHexOfTheSha256AndToStringNeverShowsTheTicketId()
        {
            JoinTicket ticket = JoinTicket.ForMatch("q3Vx9mKc0TzR1bN4wYp7Lg", "a1", "anon:p1", 3);
            Assert.That(ticket.TicketRef, Does.Match("^[0-9a-f]{12}$"));
            Assert.That(ticket.TicketRef, Is.EqualTo(SecureIds.Ref("q3Vx9mKc0TzR1bN4wYp7Lg")));
            // Known answer: SHA-256("abc") starts ba7816bf8f01.
            Assert.That(JoinTicket.ForMatch("abc", "a1", "p1", 3).TicketRef, Is.EqualTo("ba7816bf8f01"));
            Assert.That(ticket.ToString(), Does.Not.Contain("q3Vx9mKc0TzR1bN4wYp7Lg"));
            Assert.That(ticket.ToString(), Does.Contain(ticket.TicketRef));
        }

        [Test]
        public void TheBaseTicketOfTheMalformedCasesIsValid()
        {
            Assert.That(JoinTicketCodec.Decode(Encoding.UTF8.GetBytes(ValidMatch)).IsValid, Is.True);
        }

        private static IEnumerable<TestCaseData> Malformed()
        {
            yield return Text("an empty object", "{}", JoinRejectReason.PayloadInvalid);
            yield return Text("a JSON array", "[" + ValidMatch + "]", JoinRejectReason.PayloadMalformed);
            yield return Text("a JSON string", "\"match\"", JoinRejectReason.PayloadMalformed);
            yield return Text("a bare number", "1", JoinRejectReason.PayloadMalformed);
            yield return Text("truncated JSON", ValidMatch.Substring(0, ValidMatch.Length - 1), JoinRejectReason.PayloadMalformed);
            yield return Text("a second object after the first", ValidMatch + ValidMatch, JoinRejectReason.PayloadMalformed);
            yield return Text("a duplicate key", ValidMatch.Replace("\"v\":1,", "\"v\":1,\"v\":1,"), JoinRejectReason.PayloadMalformed);
            yield return Text("a duplicate playerId", ValidMatch.Replace("}", ",\"playerId\":\"anon:p2\"}"), JoinRejectReason.PayloadMalformed);
            yield return Text("single-quoted strings", ValidMatch.Replace("\"kind\":\"match\"", "'kind':'match'"), JoinRejectReason.PayloadMalformed);
            yield return Text("an unquoted key", ValidMatch.Replace("\"kind\"", "kind"), JoinRejectReason.PayloadMalformed);
            yield return Text("a trailing comma", ValidMatch.Replace("}", ",}"), JoinRejectReason.PayloadMalformed);
            yield return Text("a comment after the object", ValidMatch + " // note", JoinRejectReason.PayloadMalformed);
            yield return Text("a comment inside the object", ValidMatch.Replace("{", "{/* x */"), JoinRejectReason.PayloadMalformed);
            yield return Text("NaN for protocolVersion", ValidMatch.Replace("\"protocolVersion\":3", "\"protocolVersion\":NaN"), JoinRejectReason.PayloadMalformed);
            yield return Text("a leading zero", ValidMatch.Replace("\"protocolVersion\":3", "\"protocolVersion\":03"), JoinRejectReason.PayloadMalformed);
            yield return Text("a raw control character in a string", ValidMatch.Replace("anon:p1", "anon:\u0001p1"), JoinRejectReason.PayloadMalformed);
            yield return Text("a bad escape", ValidMatch.Replace("anon:p1", "anon:\\qp1"), JoinRejectReason.PayloadMalformed);
            yield return Text("a byte order mark", "\uFEFF" + ValidMatch, JoinRejectReason.PayloadMalformed);
            yield return Text("no v", ValidMatch.Replace("\"v\":1,", string.Empty), JoinRejectReason.PayloadInvalid);
            yield return Text("v as a string", ValidMatch.Replace("\"v\":1", "\"v\":\"1\""), JoinRejectReason.PayloadInvalid);
            yield return Text("v as a float", ValidMatch.Replace("\"v\":1", "\"v\":1.0"), JoinRejectReason.PayloadInvalid);
            yield return Text("v 2", ValidMatch.Replace("\"v\":1", "\"v\":2"), JoinRejectReason.UnsupportedVersion);
            yield return Text("v 2 with an unknown field still reads as a version problem",
                ValidMatch.Replace("\"v\":1", "\"v\":2,\"party\":[]"), JoinRejectReason.UnsupportedVersion);
            yield return Text("v 0", ValidMatch.Replace("\"v\":1", "\"v\":0"), JoinRejectReason.UnsupportedVersion);
            yield return Text("v beyond the long range", ValidMatch.Replace("\"v\":1", "\"v\":99999999999999999999999"), JoinRejectReason.PayloadInvalid);
            yield return Text("an unknown field", ValidMatch.Replace("{", "{\"token\":\"x\","), JoinRejectReason.PayloadInvalid);
            yield return Text("no kind", ValidMatch.Replace("\"kind\":\"match\",", string.Empty), JoinRejectReason.PayloadInvalid);
            yield return Text("an unknown kind", ValidMatch.Replace("\"match\"", "\"quickjoin\""), JoinRejectReason.PayloadInvalid);
            yield return Text("kind in upper case", ValidMatch.Replace("\"match\"", "\"MATCH\""), JoinRejectReason.PayloadInvalid);
            yield return Text("no protocolVersion", ValidMatch.Replace("\"protocolVersion\":3,", string.Empty), JoinRejectReason.PayloadInvalid);
            yield return Text("a negative protocolVersion", ValidMatch.Replace("\"protocolVersion\":3", "\"protocolVersion\":-1"), JoinRejectReason.PayloadInvalid);
            yield return Text("a float protocolVersion", ValidMatch.Replace("\"protocolVersion\":3", "\"protocolVersion\":3.5"), JoinRejectReason.PayloadInvalid);
            yield return Text("a protocolVersion beyond int", ValidMatch.Replace("\"protocolVersion\":3", "\"protocolVersion\":4294967296"), JoinRejectReason.PayloadInvalid);
            yield return Text("no playerId", ValidMatch.Replace("\"playerId\":\"anon:p1\",", string.Empty), JoinRejectReason.PayloadInvalid);
            yield return Text("an empty playerId", ValidMatch.Replace("\"anon:p1\"", "\"\""), JoinRejectReason.PayloadInvalid);
            yield return Text("a 101-character playerId", ValidMatch.Replace("\"anon:p1\"", "\"" + new string('p', 101) + "\""), JoinRejectReason.PayloadInvalid);
            yield return Text("a numeric playerId", ValidMatch.Replace("\"anon:p1\"", "7"), JoinRejectReason.PayloadInvalid);
            yield return Text("a null playerId", ValidMatch.Replace("\"anon:p1\"", "null"), JoinRejectReason.PayloadInvalid);
            yield return Text("a match without ticketId", ValidMatch.Replace("\"ticketId\":\"t1\",", string.Empty), JoinRejectReason.PayloadInvalid);
            yield return Text("a match without allocationId", ValidMatch.Replace(",\"allocationId\":\"a1\"", string.Empty), JoinRejectReason.PayloadInvalid);
            yield return Text("a backfill without allocationId",
                ValidMatch.Replace("\"match\"", "\"backfill\"").Replace(",\"allocationId\":\"a1\"", string.Empty), JoinRejectReason.PayloadInvalid);
            yield return Text("a reservation without reservationId", ValidMatch.Replace("\"match\"", "\"reservation\""), JoinRejectReason.PayloadInvalid);
            yield return Text("an empty allocationId", ValidMatch.Replace("\"a1\"", "\"\""), JoinRejectReason.PayloadInvalid);
            yield return Text("a 33-character displayName", ValidMatch.Replace("}", ",\"displayName\":\"" + new string('d', 33) + "\"}"), JoinRejectReason.PayloadInvalid);
            yield return new TestCaseData(new byte[0], JoinRejectReason.PayloadEmpty).SetName("an empty payload");
            yield return new TestCaseData(null, JoinRejectReason.PayloadEmpty).SetName("no payload");
            yield return new TestCaseData(Pad(ValidMatch, JoinTicketCodec.MaxPayloadBytes + 1), JoinRejectReason.PayloadTooLarge)
                .SetName("1025 bytes");
            byte[] invalidUtf8 = Encoding.UTF8.GetBytes(ValidMatch);
            invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'p')] = 0xC3; // a lead byte with no continuation
            yield return new TestCaseData(invalidUtf8, JoinRejectReason.PayloadMalformed).SetName("invalid UTF-8");
        }

        [TestCaseSource(nameof(Malformed))]
        public void AMalformedPayloadIsRejectedWithItsReason(byte[] payload, JoinRejectReason expected)
        {
            JoinTicketParseResult result = JoinTicketCodec.Decode(payload);
            Assert.That(result.IsValid, Is.False, "decoded when it should not");
            Assert.That(result.Error, Is.EqualTo(expected), result.Detail);
            Assert.That(result.Detail, Is.Not.Empty);
        }

        [Test]
        public void StrictJsonStillAcceptsEscapesExponentsAndWhitespace()
        {
            string text = "\r\n { \"v\" : 1 , \"kind\":\"lan\",\"protocolVersion\":3,\"playerId\":\"lan\\u002d1\\/x\",\"displayName\":\"\\\"Q\\\" \\t\"}\n";
            JoinTicketParseResult result = JoinTicketCodec.Decode(Encoding.UTF8.GetBytes(text));
            Assert.That(result.IsValid, Is.True, result.Detail);
            Assert.That(result.Ticket.PlayerId, Is.EqualTo("lan-1/x"));
            Assert.That(result.Ticket.DisplayName, Is.EqualTo("\"Q\" \t"));
        }

        [Test]
        public void APayloadOfExactlyTheCapIsAccepted()
        {
            byte[] payload = Pad(ValidMatch, JoinTicketCodec.MaxPayloadBytes);
            Assert.That(payload.Length, Is.EqualTo(1024));
            Assert.That(JoinTicketCodec.Decode(payload).IsValid, Is.True);
        }

        [Test]
        public void ADateShapedIdStaysAString()
        {
            byte[] payload = Encoding.UTF8.GetBytes(ValidMatch.Replace("\"a1\"", "\"2026-10-02T12:00:00Z\""));
            JoinTicketParseResult result = JoinTicketCodec.Decode(payload);
            Assert.That(result.IsValid, Is.True, result.Detail);
            Assert.That(result.Ticket.AllocationId, Is.EqualTo("2026-10-02T12:00:00Z"));
        }

        [Test]
        public void LengthsCountCharactersNotUtf16Units()
        {
            // 32 emoji are 64 UTF-16 units but 32 characters: within the displayName limit; 33 are not.
            string name = string.Concat(Enumerable.Repeat("\U0001F680", 32));
            Assert.That(JoinTicketCodec.Decode(Encoding.UTF8.GetBytes(ValidMatch.Replace("}", ",\"displayName\":\"" + name + "\"}"))).IsValid, Is.True);
            Assert.That(JoinTicketCodec.Decode(Encoding.UTF8.GetBytes(ValidMatch.Replace("}", ",\"displayName\":\"" + name + "\U0001F680\"}"))).Error,
                Is.EqualTo(JoinRejectReason.PayloadInvalid));
        }

        [Test]
        public void TheDetailNeverQuotesAPayloadValue()
        {
            const string marker = "secret-looking-value";
            byte[] payload = Encoding.UTF8.GetBytes(ValidMatch.Replace("\"t1\"", "7").Replace("\"anon:p1\"", "\"" + marker + new string('x', 100) + "\""));
            JoinTicketParseResult result = JoinTicketCodec.Decode(payload);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Detail, Does.Not.Contain(marker));
        }

        private static string Fields(JoinTicket t) =>
            string.Join("|", t.Kind, t.ProtocolVersion, t.PlayerId, t.ReservationId, t.TicketId, t.AllocationId, t.DisplayName);

        private static TestCaseData Text(string name, string json, JoinRejectReason expected) =>
            new TestCaseData(Encoding.UTF8.GetBytes(json), expected).SetName(name);

        /// <summary>The ticket with trailing JSON whitespace up to exactly <paramref name="bytes"/> bytes.</summary>
        private static byte[] Pad(string json, int bytes)
        {
            int current = Encoding.UTF8.GetByteCount(json);
            return Encoding.UTF8.GetBytes(json + new string(' ', bytes - current));
        }
    }
}
