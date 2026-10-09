using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>
    /// Quick join: hold seats on the best visible game server that fits. <c>idempotencyKey</c> is
    /// required; the SDK mints one when the caller gives none and reuses it on its own retry, so a
    /// replay answers the original hold (<c>replayed: true</c>). Unset fields are not sent.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/quick-join", WireDirection.Request, 0)]
    public sealed class QuickJoinRequest
    {
        /// <summary><c>idempotencyKey</c>: 1 to 100 characters.</summary>
        [JsonProperty("idempotencyKey", Required = Required.Always)]
        public string IdempotencyKey { get; set; }

        /// <summary><c>seats</c>.</summary>
        [JsonProperty("seats", NullValueHandling = NullValueHandling.Ignore)]
        public int? Seats { get; set; }

        /// <summary><c>playerIds</c>: when present, exactly <c>seats</c> entries.</summary>
        [JsonProperty("playerIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> PlayerIds { get; set; }

        /// <summary><c>filters</c>: version and meta conditions.</summary>
        [JsonProperty("filters", NullValueHandling = NullValueHandling.Ignore)]
        public QuickJoinFilters Filters { get; set; }

        /// <summary><c>context</c>: opaque object delivered to the reserved game server.</summary>
        [JsonProperty("context", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Context { get; set; }

        /// <summary><c>latency</c>: measured round trip in ms per location id, at most 32 entries.</summary>
        [JsonProperty("latency", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, int> Latency { get; set; }

        /// <summary><c>maxLatencyMs</c>: hard ceiling; requires <see cref="Latency"/>.</summary>
        [JsonProperty("maxLatencyMs", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxLatencyMs { get; set; }
    }

    /// <summary>
    /// Quick-join <c>filters</c>: an exact <c>version</c>, and <c>meta</c> conditions in the server
    /// list's operator model (a scalar is an exact match, an object is <c>{operator: value}</c>).
    /// Build meta with <see cref="WithMeta(string, string)"/> and <see cref="WithMetaWhere(string, MetaOp, string)"/>.
    /// </summary>
    [Preserve]
    public sealed class QuickJoinFilters
    {
        /// <summary><c>version</c>: exact match on the reported version.</summary>
        [JsonProperty("version", NullValueHandling = NullValueHandling.Ignore)]
        public string Version { get; set; }

        /// <summary><c>meta</c>: per key a string, number or boolean (exact), or an operator object.</summary>
        [JsonProperty("meta", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, JToken> Meta { get; set; }

        /// <summary>Adds an exact string match on <paramref name="key"/>.</summary>
        public QuickJoinFilters WithMeta(string key, string value) => SetExact(key, new JValue(value ?? throw new ArgumentNullException(nameof(value))));

        /// <summary>Adds an exact numeric match on <paramref name="key"/>.</summary>
        public QuickJoinFilters WithMeta(string key, double value) => SetExact(key, new JValue(Finite(value)));

        /// <summary>Adds an exact integer match on <paramref name="key"/>.</summary>
        public QuickJoinFilters WithMeta(string key, long value) => SetExact(key, new JValue(value));

        /// <summary>Adds an exact boolean match on <paramref name="key"/>.</summary>
        public QuickJoinFilters WithMeta(string key, bool value) => SetExact(key, new JValue(value));

        /// <summary>Adds an operator condition on <paramref name="key"/> (several operators per key combine).</summary>
        public QuickJoinFilters WithMetaWhere(string key, MetaOp op, string value) => SetOperator(key, op, new JValue(value ?? throw new ArgumentNullException(nameof(value))));

        /// <summary>Adds a numeric operator condition on <paramref name="key"/>.</summary>
        public QuickJoinFilters WithMetaWhere(string key, MetaOp op, double value) => SetOperator(key, op, new JValue(Finite(value)));

        /// <summary>Adds an integer operator condition on <paramref name="key"/>.</summary>
        public QuickJoinFilters WithMetaWhere(string key, MetaOp op, long value) => SetOperator(key, op, new JValue(value));

        /// <summary>Adds a boolean operator condition on <paramref name="key"/>.</summary>
        public QuickJoinFilters WithMetaWhere(string key, MetaOp op, bool value) => SetOperator(key, op, new JValue(value));

        private QuickJoinFilters SetExact(string key, JToken value)
        {
            MetaKeys.Require(key);
            Meta = Meta ?? new Dictionary<string, JToken>(StringComparer.Ordinal);
            Meta[key] = value;
            return this;
        }

        private QuickJoinFilters SetOperator(string key, MetaOp op, JToken value)
        {
            MetaKeys.Require(key);
            Meta = Meta ?? new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (!(Meta.TryGetValue(key, out JToken existing) && existing is JObject conditions))
            {
                conditions = new JObject();
                Meta[key] = conditions;
            }

            conditions[MetaOps.Wire(op)] = value;
            return this;
        }

        private static double Finite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "meta values must be finite numbers");
            }

            return value;
        }
    }

    /// <summary>Meta key rules shared by the server list query and quick join.</summary>
    internal static class MetaKeys
    {
        /// <summary>Throws unless <paramref name="key"/> matches <c>^[A-Za-z0-9_.-]{1,50}$</c>.</summary>
        public static void Require(string key)
        {
            if (!IsValid(key))
            {
                throw new ArgumentException("a meta key is 1 to 50 characters from [A-Za-z0-9_.-]", nameof(key));
            }
        }

        /// <summary>True when <paramref name="key"/> matches <c>^[A-Za-z0-9_.-]{1,50}$</c>.</summary>
        public static bool IsValid(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 50)
            {
                return false;
            }

            foreach (char c in key)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Invariant text for a number in a query string.</summary>
        public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
