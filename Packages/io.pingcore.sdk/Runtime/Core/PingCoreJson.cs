using Newtonsoft.Json;

namespace PingCore.Core
{
    /// <summary>
    /// The one JSON configuration for every PingCore wire body. Every DTO property names its
    /// JSON key and its null handling explicitly, so these settings only fix the parser
    /// behaviour that must not vary: no type metadata, no date coercion, bounded depth.
    /// </summary>
    public static class PingCoreJson
    {
        /// <summary>
        /// Runtime settings. Unknown members are ignored, because the platform contracts are
        /// additive and a new field must never break a shipped game. Each access returns a
        /// fresh instance, so no caller can change the settings another caller sees.
        /// </summary>
        public static JsonSerializerSettings Settings => Create(MissingMemberHandling.Ignore);

        /// <summary>
        /// Strict settings for contract tests: an unknown member is an error, so a fixture key
        /// the DTO does not model fails the round trip instead of disappearing.
        /// </summary>
        public static JsonSerializerSettings CreateStrictSettings() => Create(MissingMemberHandling.Error);

        private static JsonSerializerSettings Create(MissingMemberHandling missingMemberHandling)
        {
            return new JsonSerializerSettings
            {
                MissingMemberHandling = missingMemberHandling,
                NullValueHandling = NullValueHandling.Ignore,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                TypeNameHandling = TypeNameHandling.None,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                MaxDepth = 64,
                Formatting = Formatting.None,
            };
        }
    }
}
