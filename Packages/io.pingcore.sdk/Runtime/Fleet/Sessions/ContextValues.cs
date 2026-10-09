using Newtonsoft.Json.Linq;

namespace PingCore.Fleet.Sessions
{
    /// <summary>Lenient typed reads from an allocation or backfill context: a missing or mistyped value is null, never an exception.</summary>
    internal static class ContextValues
    {
        public static string String(JObject context, string key)
        {
            JToken token = context?[key];
            return token != null && token.Type == JTokenType.String ? (string)token : null;
        }

        public static int? Int(JObject context, string key)
        {
            JToken token = context?[key];
            if (token == null || token.Type != JTokenType.Integer || !(((JValue)token).Value is long value) || value < int.MinValue || value > int.MaxValue)
            {
                return null;
            }

            return (int)value;
        }

        public static bool Bool(JObject context, string key)
        {
            JToken token = context?[key];
            return token != null && token.Type == JTokenType.Boolean && (bool)token;
        }
    }
}
