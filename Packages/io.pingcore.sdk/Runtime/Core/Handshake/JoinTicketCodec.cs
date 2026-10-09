using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// Encodes and decodes the v1 join ticket (<c>contracts/handshake/join-ticket.v1.schema.json</c>):
    /// at most <see cref="MaxPayloadBytes"/> bytes of strict UTF-8 holding one RFC 8259 JSON object with
    /// no duplicate and no unknown keys, the schema's fields, types and lengths (in code points), and
    /// the per-kind ids. A <c>v</c> other than 1 is <see cref="JoinRejectReason.UnsupportedVersion"/>
    /// before any other field is judged, so a later client is told so. Pure: no engine types, no I/O.
    /// </summary>
    public static class JoinTicketCodec
    {
        /// <summary>The size cap of the UTF-8 payload, so a join ticket fits one Unity Transport packet.</summary>
        public const int MaxPayloadBytes = 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "v", "kind", "protocolVersion", "playerId", "reservationId", "ticketId", "allocationId", "displayName",
        };

        private static readonly JsonLoadSettings LoadSettings = new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            LineInfoHandling = LineInfoHandling.Ignore,
            CommentHandling = CommentHandling.Load,
        };

        /// <summary>
        /// The compact UTF-8 JSON payload, keys in schema order. Refuses (throws) a ticket whose payload
        /// would be over <see cref="MaxPayloadBytes"/> or is not valid Unicode, so a client never sends one
        /// the game server must reject.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="ticket"/> is null.</exception>
        /// <exception cref="ArgumentException">The payload would break the cap or the schema.</exception>
        public static byte[] Encode(JoinTicket ticket)
        {
            if (!TryEncode(ticket, out byte[] payload, out string problem))
            {
                throw new ArgumentException(problem, nameof(ticket));
            }

            return payload;
        }

        /// <summary>As <see cref="Encode"/>, returning false and why instead of throwing. The problem never quotes a field value.</summary>
        public static bool TryEncode(JoinTicket ticket, out byte[] payload, out string problem)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            payload = null;
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var writer = new JsonTextWriter(text))
            {
                writer.Formatting = Formatting.None;
                writer.WriteStartObject();
                writer.WritePropertyName("v");
                writer.WriteValue(1);
                writer.WritePropertyName("kind");
                writer.WriteValue(JoinTicketKinds.ToWire(ticket.Kind));
                writer.WritePropertyName("protocolVersion");
                writer.WriteValue(ticket.ProtocolVersion);
                writer.WritePropertyName("playerId");
                writer.WriteValue(ticket.PlayerId);
                WriteOptional(writer, "reservationId", ticket.ReservationId);
                WriteOptional(writer, "ticketId", ticket.TicketId);
                WriteOptional(writer, "allocationId", ticket.AllocationId);
                WriteOptional(writer, "displayName", ticket.DisplayName);
                writer.WriteEndObject();
            }

            byte[] bytes;
            try
            {
                bytes = StrictUtf8.GetBytes(text.ToString());
            }
            catch (ArgumentException)
            {
                problem = "a field is not valid Unicode (a lone surrogate)";
                return false;
            }

            if (bytes.Length > MaxPayloadBytes)
            {
                problem = "the join ticket is " + bytes.Length + " bytes; the cap is " + MaxPayloadBytes;
                return false;
            }

            JoinTicketParseResult check = Decode(bytes);
            if (!check.IsValid)
            {
                problem = "the join ticket would be refused as " + JoinRejectReasons.ToWire(check.Error) + ": " + check.Detail;
                return false;
            }

            payload = bytes;
            problem = null;
            return true;
        }

        /// <summary>Decodes a raw connection payload. Never throws.</summary>
        public static JoinTicketParseResult Decode(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return JoinTicketParseResult.Fail(JoinRejectReason.PayloadEmpty, "the connection carried no payload");
            }

            if (payload.Length > MaxPayloadBytes)
            {
                return JoinTicketParseResult.Fail(JoinRejectReason.PayloadTooLarge,
                    "the payload is " + payload.Length + " bytes; the cap is " + MaxPayloadBytes);
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(payload);
            }
            catch (ArgumentException)
            {
                return JoinTicketParseResult.Fail(JoinRejectReason.PayloadMalformed, "the payload is not valid UTF-8");
            }

            if (!StrictJsonSyntax.IsSingleObject(text))
            {
                return JoinTicketParseResult.Fail(JoinRejectReason.PayloadMalformed, "the payload is not one RFC 8259 JSON object");
            }

            JObject root;
            try
            {
                root = ReadSingleObject(text);
            }
            catch (JsonException)
            {
                return JoinTicketParseResult.Fail(JoinRejectReason.PayloadMalformed, "the payload is not one JSON object with unique keys");
            }

            if (root == null)
            {
                return JoinTicketParseResult.Fail(JoinRejectReason.PayloadMalformed, "the payload is not a JSON object");
            }

            return Validate(root);
        }

        /// <summary>JSON Schema lengths count characters (code points), not UTF-16 units.</summary>
        internal static int CountCodePoints(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsLowSurrogate(text[i]) || i == 0 || !char.IsHighSurrogate(text[i - 1]))
                {
                    count++;
                }
            }

            return count;
        }

        private static void WriteOptional(JsonTextWriter writer, string key, string value)
        {
            if (value != null)
            {
                writer.WritePropertyName(key);
                writer.WriteValue(value);
            }
        }

        private static JObject ReadSingleObject(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = 64;
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                {
                    return null;
                }

                JObject root = JObject.Load(reader, LoadSettings);
                if (reader.Read())
                {
                    // Content after the object: not one JSON object.
                    return null;
                }

                return root;
            }
        }

        private static JoinTicketParseResult Validate(JObject root)
        {
            JToken version = root["v"];
            if (version == null)
            {
                return Invalid("v is required");
            }

            if (!TryGetInteger(version, out long versionNumber))
            {
                return Invalid("v must be an integer");
            }

            if (versionNumber != 1)
            {
                return JoinTicketParseResult.Fail(JoinRejectReason.UnsupportedVersion, "v " + versionNumber + " is not supported; this game server reads v 1");
            }

            foreach (JProperty property in root.Properties())
            {
                if (!KnownKeys.Contains(property.Name))
                {
                    return Invalid("unknown field (the v1 schema allows no additional properties)");
                }
            }

            JToken kindToken = root["kind"];
            if (kindToken == null)
            {
                return Invalid("kind is required");
            }

            if (kindToken.Type != JTokenType.String || !JoinTicketKinds.TryParse(kindToken.Value<string>(), out JoinTicketKind kind))
            {
                return Invalid("kind must be reservation, match, backfill or lan");
            }

            JToken protocol = root["protocolVersion"];
            if (protocol == null)
            {
                return Invalid("protocolVersion is required");
            }

            if (!TryGetInteger(protocol, out long protocolNumber) || protocolNumber < 0 || protocolNumber > int.MaxValue)
            {
                return Invalid("protocolVersion must be an integer from 0");
            }

            if (!TryGetString(root, "playerId", true, 1, JoinTicket.MaxIdLength, out string playerId, out string problem)
                || !TryGetString(root, "reservationId", false, 1, JoinTicket.MaxIdLength, out string reservationId, out problem)
                || !TryGetString(root, "ticketId", false, 1, JoinTicket.MaxIdLength, out string ticketId, out problem)
                || !TryGetString(root, "allocationId", false, 1, JoinTicket.MaxIdLength, out string allocationId, out problem)
                || !TryGetString(root, "displayName", false, 0, JoinTicket.MaxDisplayNameLength, out string displayName, out problem))
            {
                return Invalid(problem);
            }

            if (kind == JoinTicketKind.Reservation && reservationId == null)
            {
                return Invalid("reservationId is required for kind reservation");
            }

            if ((kind == JoinTicketKind.Match || kind == JoinTicketKind.Backfill) && (ticketId == null || allocationId == null))
            {
                return Invalid("ticketId and allocationId are required for kind match and backfill");
            }

            return JoinTicketParseResult.Ok(new JoinTicket(kind, (int)protocolNumber, playerId, reservationId, ticketId, allocationId, displayName));
        }

        private static JoinTicketParseResult Invalid(string detail) => JoinTicketParseResult.Fail(JoinRejectReason.PayloadInvalid, detail);

        private static bool TryGetInteger(JToken token, out long value)
        {
            value = 0;
            if (token.Type != JTokenType.Integer || !(token is JValue jValue) || !(jValue.Value is long number))
            {
                // A JSON integer beyond the long range parses as a BigInteger: never a valid v or protocolVersion.
                return false;
            }

            value = number;
            return true;
        }

        private static bool TryGetString(JObject root, string key, bool required, int minLength, int maxLength, out string value, out string problem)
        {
            value = null;
            problem = null;
            JToken token = root[key];
            if (token == null)
            {
                if (required)
                {
                    problem = key + " is required";
                    return false;
                }

                return true;
            }

            if (token.Type != JTokenType.String)
            {
                problem = key + " must be a string";
                return false;
            }

            string text = token.Value<string>();
            int length = CountCodePoints(text);
            if (length < minLength || length > maxLength)
            {
                problem = key + " must be " + minLength + " to " + maxLength + " characters";
                return false;
            }

            value = text;
            return true;
        }
    }
}
