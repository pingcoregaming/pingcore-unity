using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Fleet
{
    /// <summary>Wire constants and pure parsers for the local SDK endpoint.</summary>
    internal static class LocalSdkValues
    {
        /// <summary>The one environment variable the shim reads.</summary>
        public const string PortVariable = LocalSdkPort.Variable;

        /// <summary>Annotation carrying the allocation id on the GameServer view.</summary>
        public const string AllocationIdAnnotation = "pingcore.io/allocation-id";

        /// <summary>Annotation carrying the allocation context as JSON inside a string.</summary>
        public const string AllocationContextAnnotation = "pingcore.io/allocation-context";

        /// <summary>Annotation carrying the latest backfill id.</summary>
        public const string BackfillIdAnnotation = "pingcore.io/backfill-id";

        /// <summary>Annotation carrying the latest backfill context as JSON inside a string.</summary>
        public const string BackfillContextAnnotation = "pingcore.io/backfill-context";

        /// <summary>The runtime JSON settings, captured once (unknown members ignored).</summary>
        public static readonly JsonSerializerSettings Json = PingCoreJson.Settings;

        /// <summary>Parses the port variable: <see cref="LocalSdkPort.TryParse"/>, the one parse the heartbeat tier shares.</summary>
        public static bool TryParsePort(string raw, out int port) => LocalSdkPort.TryParse(raw, out port);

        /// <summary>Parses an Agones int64 string (optional leading minus, ASCII digits); null when absent or malformed.</summary>
        public static long? ParseInt64(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            int start = value[0] == '-' ? 1 : 0;
            if (start == value.Length)
            {
                return null;
            }

            for (int i = start; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return null;
                }
            }

            return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) ? parsed : (long?)null;
        }

        /// <summary>Parses text as a JSON object without date or float coercion; null when it is not one.</summary>
        public static JObject TryParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 64 })
                {
                    JToken token = JToken.ReadFrom(reader);
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.Comment)
                        {
                            return null;
                        }
                    }

                    return token as JObject;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Deserialises a body with the runtime settings; null and a reason when it does not parse.</summary>
        public static T TryDeserialize<T>(string body, out string problem)
            where T : class
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(body))
            {
                problem = "empty body";
                return null;
            }

            try
            {
                T value = JsonConvert.DeserializeObject<T>(body, Json);
                if (value == null)
                {
                    problem = "null body";
                }

                return value;
            }
            catch (JsonException e)
            {
                // The path only: a conversion message can quote a value from the body.
                problem = e.GetType().Name + " at '" + ((e as JsonSerializationException)?.Path ?? (e as JsonReaderException)?.Path ?? "?") + "'";
                return null;
            }
        }

        /// <summary>Serialises a request body with the runtime settings.</summary>
        public static string Serialize(object body) => JsonConvert.SerializeObject(body, Json);

        /// <summary>Escapes one path segment.</summary>
        public static string Segment(string value) => Uri.EscapeDataString(value);
    }
}
