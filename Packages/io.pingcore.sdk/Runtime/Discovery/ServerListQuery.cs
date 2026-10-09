using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The public server list query, built fluently and rendered by the pure
    /// <see cref="ToQueryString"/> in a fixed order: <c>limit</c>, <c>offset</c>, <c>search</c>,
    /// <c>version</c>, <c>hasSlots</c>, the meta conditions in the order added, <c>sort</c>, the
    /// <c>latency.&lt;id&gt;</c> entries by id, <c>maxLatencyMs</c>. Keys and values are
    /// percent-encoded; operator brackets stay literal (<c>meta.xp[gt]=10</c>), which Discovery's
    /// parser reads as is. A parameter Discovery
    /// does not know is ignored there, never rejected, so a newer client degrades to "no filter".
    /// Invalid arguments throw <see cref="ArgumentException"/>; they are programming errors.
    /// </summary>
    public sealed class ServerListQuery
    {
        /// <summary>Most <c>latency.&lt;id&gt;</c> entries Discovery reads.</summary>
        public const int MaxLatencyEntries = 32;

        /// <summary>Largest page Discovery serves.</summary>
        public const int MaxLimit = 500;

        private readonly List<KeyValuePair<string, string>> meta = new List<KeyValuePair<string, string>>();
        private readonly Dictionary<string, int> latency = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool? hasSlots;
        private string version;
        private string search;
        private string sort;
        private int? maxLatencyMs;

        /// <summary>The page size set by <see cref="Page"/>, or null for Discovery's default (100).</summary>
        public int? Limit { get; private set; }

        /// <summary>The offset set by <see cref="Page"/>, or null for 0.</summary>
        public int? Offset { get; private set; }

        /// <summary><c>hasSlots</c>: true keeps game servers with room, false only full ones.</summary>
        public ServerListQuery HasSlots(bool value)
        {
            hasSlots = value;
            return this;
        }

        /// <summary><c>version</c>: exact match on the reported version; null clears it.</summary>
        public ServerListQuery Version(string value)
        {
            version = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            return this;
        }

        /// <summary><c>search</c>: case-insensitive substring of the name; null clears it.</summary>
        public ServerListQuery Search(string value)
        {
            search = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            return this;
        }

        /// <summary><c>meta.&lt;key&gt;=value</c>: case-insensitive exact string match.</summary>
        public ServerListQuery Meta(string key, string value) => AddMeta(key, null, Text(value));

        /// <summary><c>meta.&lt;key&gt;=value</c> for a number (compared as a string by Discovery's exact match).</summary>
        public ServerListQuery Meta(string key, double value) => AddMeta(key, null, Number(value));

        /// <summary><c>meta.&lt;key&gt;=value</c> for an integer.</summary>
        public ServerListQuery Meta(string key, long value) => AddMeta(key, null, value.ToString(CultureInfo.InvariantCulture));

        /// <summary><c>meta.&lt;key&gt;=true|false</c>.</summary>
        public ServerListQuery Meta(string key, bool value) => AddMeta(key, null, value ? "true" : "false");

        /// <summary><c>meta.&lt;key&gt;[op]=value</c>. For <see cref="MetaOp.In"/> the value is a comma-separated list.</summary>
        public ServerListQuery MetaWhere(string key, MetaOp op, string value) => AddMeta(key, MetaOps.Wire(op), Text(value));

        /// <summary><c>meta.&lt;key&gt;[op]=value</c> for a number.</summary>
        public ServerListQuery MetaWhere(string key, MetaOp op, double value) => AddMeta(key, MetaOps.Wire(op), Number(value));

        /// <summary><c>meta.&lt;key&gt;[op]=value</c> for an integer.</summary>
        public ServerListQuery MetaWhere(string key, MetaOp op, long value) => AddMeta(key, MetaOps.Wire(op), value.ToString(CultureInfo.InvariantCulture));

        /// <summary><c>meta.&lt;key&gt;[op]=true|false</c>.</summary>
        public ServerListQuery MetaWhere(string key, MetaOp op, bool value) => AddMeta(key, MetaOps.Wire(op), value ? "true" : "false");

        /// <summary><c>meta.&lt;key&gt;[in]=a,b,c</c>. Entries must not contain a comma.</summary>
        public ServerListQuery MetaWhere(string key, MetaOp op, IEnumerable<string> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            List<string> list = values.ToList();
            if (list.Count == 0 || list.Any(v => string.IsNullOrEmpty(v) || v.IndexOf(',') >= 0))
            {
                throw new ArgumentException("a list condition needs at least one non-empty entry with no comma", nameof(values));
            }

            return AddMeta(key, MetaOps.Wire(op), string.Join(",", list));
        }

        /// <summary><c>sort</c> by a built-in key; <paramref name="descending"/> prefixes <c>-</c>.</summary>
        public ServerListQuery SortBy(ServerSort field, bool descending = false)
        {
            string name;
            switch (field)
            {
                case ServerSort.Players: name = "players"; break;
                case ServerSort.Name: name = "name"; break;
                case ServerSort.UpdatedAt: name = "updatedAt"; break;
                case ServerSort.Latency: name = "latency"; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }

            sort = (descending ? "-" : string.Empty) + name;
            return this;
        }

        /// <summary><c>sort=meta.&lt;key&gt;</c> (numeric-aware on the service).</summary>
        public ServerListQuery SortByMeta(string key, bool descending = false)
        {
            MetaKeys.Require(key);
            sort = (descending ? "-" : string.Empty) + "meta." + key;
            return this;
        }

        /// <summary>
        /// <c>latency.&lt;id&gt;=ms</c> per measured location, replacing any earlier map. Above
        /// <see cref="MaxLatencyEntries"/> entries only the lowest are kept (Discovery reads 32).
        /// </summary>
        public ServerListQuery WithLatency(IReadOnlyDictionary<string, int> medians)
        {
            latency.Clear();
            if (medians == null)
            {
                return this;
            }

            foreach (KeyValuePair<string, int> entry in medians.OrderBy(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).Take(MaxLatencyEntries))
            {
                if (!LatencyKeys.IsValidLocationId(entry.Key))
                {
                    throw new ArgumentException("a location id is 1 to 30 characters from [A-Za-z0-9_-]", nameof(medians));
                }

                if (entry.Value < 0 || entry.Value > LatencyKeys.MaxLatencyMs)
                {
                    throw new ArgumentOutOfRangeException(nameof(medians), "latency values are 0 to 10000 ms");
                }

                latency[entry.Key] = entry.Value;
            }

            return this;
        }

        /// <summary><c>maxLatencyMs</c>: hides game servers not measured at or below it. Only sent with <see cref="WithLatency"/>.</summary>
        public ServerListQuery MaxLatency(int milliseconds)
        {
            if (milliseconds < 1 || milliseconds > LatencyKeys.MaxLatencyMs)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds), "1 to 10000 ms");
            }

            maxLatencyMs = milliseconds;
            return this;
        }

        /// <summary><c>limit</c> (1 to 500) and <c>offset</c> (0 or more).</summary>
        public ServerListQuery Page(int limit, int offset = 0)
        {
            if (limit < 1 || limit > MaxLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "1 to 500");
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "0 or more");
            }

            Limit = limit;
            Offset = offset;
            return this;
        }

        /// <summary>An independent copy.</summary>
        public ServerListQuery Clone()
        {
            var copy = new ServerListQuery
            {
                hasSlots = hasSlots,
                version = version,
                search = search,
                sort = sort,
                maxLatencyMs = maxLatencyMs,
                Limit = Limit,
                Offset = Offset,
            };
            copy.meta.AddRange(meta);
            foreach (KeyValuePair<string, int> entry in latency)
            {
                copy.latency[entry.Key] = entry.Value;
            }

            return copy;
        }

        /// <summary>The query string without a leading <c>?</c>; empty when nothing is set. Pure.</summary>
        public string ToQueryString()
        {
            var parts = new List<string>();
            if (Limit.HasValue)
            {
                parts.Add("limit=" + Limit.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (Offset.HasValue)
            {
                parts.Add("offset=" + Offset.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (search != null)
            {
                parts.Add("search=" + Uri.EscapeDataString(search));
            }

            if (version != null)
            {
                parts.Add("version=" + Uri.EscapeDataString(version));
            }

            if (hasSlots.HasValue)
            {
                parts.Add("hasSlots=" + (hasSlots.Value ? "true" : "false"));
            }

            foreach (KeyValuePair<string, string> condition in meta)
            {
                parts.Add(condition.Key + "=" + Uri.EscapeDataString(condition.Value));
            }

            if (sort != null)
            {
                parts.Add("sort=" + Uri.EscapeDataString(sort));
            }

            foreach (KeyValuePair<string, int> entry in latency.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                parts.Add("latency." + entry.Key + "=" + entry.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (maxLatencyMs.HasValue && latency.Count > 0)
            {
                parts.Add("maxLatencyMs=" + maxLatencyMs.Value.ToString(CultureInfo.InvariantCulture));
            }

            var builder = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('&');
                }

                builder.Append(parts[i]);
            }

            return builder.ToString();
        }

        /// <inheritdoc />
        public override string ToString() => ToQueryString();

        private ServerListQuery AddMeta(string key, string op, string value)
        {
            MetaKeys.Require(key);
            string name = "meta." + key + (op != null ? "[" + op + "]" : string.Empty);
            meta.Add(new KeyValuePair<string, string>(name, value));
            return this;
        }

        private static string Text(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("a meta value must not be empty", nameof(value));
            }

            return value;
        }

        private static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "meta values must be finite numbers");
            }

            return MetaKeys.Number(value);
        }
    }
}
