using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// Reads a JSON object of numbers into <c>Dictionary&lt;string, double&gt;</c> and writes an
    /// integral value back as a JSON integer, so <c>{"skill":1210}</c> round-trips as
    /// <c>1210</c>, not <c>1210.0</c>.
    /// </summary>
    [Preserve]
    internal sealed class NumberMapConverter : JsonConverter<Dictionary<string, double>>
    {
        /// <summary>The largest magnitude written as an integer: 2^53, the last exactly representable integer.</summary>
        private const double MaxExactInteger = 9007199254740992d;

        /// <inheritdoc />
        public override Dictionary<string, double> ReadJson(JsonReader reader, Type objectType, Dictionary<string, double> existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonToken.StartObject)
            {
                throw new JsonSerializationException("Expected a JSON object of numbers.");
            }

            var map = new Dictionary<string, double>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                {
                    return map;
                }

                if (reader.TokenType != JsonToken.PropertyName)
                {
                    throw new JsonSerializationException("Expected a property name.");
                }

                string name = (string)reader.Value;
                if (!reader.Read() || (reader.TokenType != JsonToken.Integer && reader.TokenType != JsonToken.Float))
                {
                    throw new JsonSerializationException("Expected a number for attribute '" + name + "'.");
                }

                map[name] = Convert.ToDouble(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            throw new JsonSerializationException("Unterminated JSON object.");
        }

        /// <inheritdoc />
        public override void WriteJson(JsonWriter writer, Dictionary<string, double> value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            foreach (KeyValuePair<string, double> entry in value)
            {
                writer.WritePropertyName(entry.Key);
                double number = entry.Value;
                if (Math.Floor(number) == number && Math.Abs(number) <= MaxExactInteger)
                {
                    writer.WriteValue((long)number);
                }
                else
                {
                    writer.WriteValue(number);
                }
            }

            writer.WriteEndObject();
        }
    }
}
