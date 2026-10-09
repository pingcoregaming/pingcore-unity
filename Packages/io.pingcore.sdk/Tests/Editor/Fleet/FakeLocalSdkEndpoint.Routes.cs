using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The fake's routes, dispatched under the lock: lifecycle, counters, sessions and joinable records, backfills, reservations, and the JSON 501 fallback.</summary>
    internal sealed partial class FakeLocalSdkEndpoint
    {
        private readonly Dictionary<string, (JObject Record, int HiddenForLookups)> reservations = new Dictionary<string, (JObject, int)>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> lookups = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Adds a reservation the lookup serves after <paramref name="hiddenForLookups"/> 404s.</summary>
        public void AddReservation(JObject record, int hiddenForLookups = 0)
        {
            lock (gate)
            {
                reservations[(string)record["reservationId"]] = (record, hiddenForLookups);
            }
        }

        private readonly Dictionary<string, List<DateTimeOffset>> lookupTimes = new Dictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);

        /// <summary>
        /// Read once per reservation lookup, under the lock, to record when it arrived. A test may
        /// also move its virtual clock here to model the time a lookup itself takes. Unset, no time is recorded.
        /// </summary>
        public Func<DateTimeOffset> ReservationLookupClock { get; set; }

        public int LookupsOf(string reservationId)
        {
            lock (gate)
            {
                return lookups.TryGetValue(reservationId, out int n) ? n : 0;
            }
        }

        /// <summary>When each lookup of <paramref name="reservationId"/> arrived, by <see cref="ReservationLookupClock"/>.</summary>
        public List<DateTimeOffset> LookupTimesOf(string reservationId)
        {
            lock (gate)
            {
                return lookupTimes.TryGetValue(reservationId, out List<DateTimeOffset> times) ? times.ToList() : new List<DateTimeOffset>();
            }
        }

        /// <summary>
        /// Returns the JSON answer, or null for the watch stream. <paramref name="routed"/> goes false for the
        /// 501 fallback, which does not integrate. Runs under the lock.
        /// </summary>
        private JToken Dispatch(string method, string path, string[] s, string body, ref int status, ref bool changed, ref bool routed)
        {
            if (method == "POST" && path == "/ready")
            {
                Ready = true;
                changed = true;
                return new JObject();
            }

            if (method == "POST" && path == "/health")
            {
                return new JObject();
            }

            if (method == "POST" && path == "/shutdown")
            {
                ShutdownRequested = true;
                changed = true;
                return new JObject();
            }

            if (method == "POST" && path == "/allocate")
            {
                allocationId = "self-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                allocationContextJson = "{}";
                changed = true;
                return new JObject();
            }

            if (method == "GET" && path == "/gameserver")
            {
                return BuildViewLocked();
            }

            if (method == "GET" && path == "/watch/gameserver")
            {
                WatchRequests++;
                if (RefuseWatch)
                {
                    status = 503;
                    return new JObject { ["error"] = "unavailable" };
                }

                return null;
            }

            if (s.Length == 3 && s[0] == "v1beta1" && s[1] == "counters")
            {
                JToken counter = Counter(method, s[2], body, ref status, ref changed);
                if (counter != null)
                {
                    return counter;
                }
            }

            if (s.Length == 4 && s[0] == "v1" && s[1] == "sessions")
            {
                JToken session = Session(method, s[2], s[3], body, ref status, ref changed);
                if (session != null)
                {
                    return session;
                }
            }

            if (method == "GET" && path == "/v1/backfills")
            {
                return new JObject { ["backfills"] = new JArray(backfills.Select(b => (JToken)b.DeepClone())) };
            }

            if (method == "GET" && path == "/pingcore/reservations")
            {
                return new JObject { ["reservations"] = new JArray(reservations.Values.Where(r => r.HiddenForLookups <= 0).Select(r => (JToken)r.Record.DeepClone())) };
            }

            if (method == "GET" && s.Length == 3 && s[0] == "pingcore" && s[1] == "reservations")
            {
                return Reservation(s[2], ref status);
            }

            routed = false;
            status = 501;
            return new JObject { ["error"] = "not_implemented" };
        }

        private JToken Counter(string method, string name, string body, ref int status, ref bool changed)
        {
            (string Name, long Capacity)? configured = FleetCounter(name);
            if (method == "GET")
            {
                if (configured == null && !sdkCounters.ContainsKey(name))
                {
                    status = 404;
                    return new JObject { ["message"] = "counter " + name + " not found" };
                }

                return CounterView(name, configured);
            }

            if (method != "PATCH")
            {
                return null;
            }

            JObject input = ParseObject(body) ?? new JObject();
            sdkCounters.TryGetValue(name, out (long? Count, long? Capacity) current);
            long? count = null;
            long? capacity = null;
            bool bad = false;
            if (input["count"] != null)
            {
                bad |= !TryInt(input["count"], out long v);
                count = v;
            }
            else if (input["countDiff"] != null)
            {
                bad |= !TryInt(input["countDiff"], out long v);
                count = (current.Count ?? 0) + v;
            }

            if (input["capacity"] != null)
            {
                bad |= !TryInt(input["capacity"], out long v);
                capacity = v;
            }

            if (bad)
            {
                status = 400;
                return new JObject { ["message"] = "count/countDiff/capacity must be integers" };
            }

            sdkCounters[name] = (count != null ? Math.Max(0, count.Value) : current.Count, capacity ?? current.Capacity);
            changed = true;
            return CounterView(name, configured);
        }

        private JToken Session(string method, string id, string action, string body, ref int status, ref bool changed)
        {
            if (method == "POST" && action == "ended")
            {
                if (id == allocationId)
                {
                    allocationId = null;
                    allocationContextJson = null;
                    Joinable = null;
                    backfills.Clear();
                }
                else
                {
                    backfills.RemoveAll(b => (string)b["allocationId"] == id);
                }

                changed = true;
                return new JObject();
            }

            if (method == "POST" && action == "joinable")
            {
                JObject input = ParseObject(body) ?? new JObject();
                if (input["openSeats"] == null || input["openSeats"].Type != JTokenType.Integer || (long)input["openSeats"] < 0)
                {
                    status = 400;
                    return new JObject { ["message"] = "openSeats must be a non-negative integer" };
                }

                var record = new JObject { ["queue"] = input["queue"] != null ? ((string)input["queue"]).Trim() : "default", ["openSeats"] = input["openSeats"] };
                if (input["sessionSize"] != null)
                {
                    record["sessionSize"] = input["sessionSize"];
                }

                if (input["attributes"] != null && input["attributes"].Type != JTokenType.Null)
                {
                    record["attributes"] = input["attributes"];
                }

                record["sessionId"] = id;
                if (input["ttlSeconds"] != null)
                {
                    record["ttlSeconds"] = input["ttlSeconds"];
                }

                Joinable = record;
                return record;
            }

            if (method == "DELETE" && action == "joinable")
            {
                Joinable = null;
                return new JObject();
            }

            return null;
        }

        private JToken Reservation(string id, ref int status)
        {
            lookups[id] = (lookups.TryGetValue(id, out int n) ? n : 0) + 1;
            if (ReservationLookupClock != null)
            {
                if (!lookupTimes.TryGetValue(id, out List<DateTimeOffset> times))
                {
                    lookupTimes[id] = times = new List<DateTimeOffset>();
                }

                times.Add(ReservationLookupClock());
            }

            if (reservations.TryGetValue(id, out (JObject Record, int HiddenForLookups) entry))
            {
                if (entry.HiddenForLookups <= 0)
                {
                    return entry.Record.DeepClone();
                }

                reservations[id] = (entry.Record, entry.HiddenForLookups - 1);
            }

            status = 404;
            return new JObject { ["message"] = "reservation not found" };
        }

        private static bool TryInt(JToken token, out long value)
        {
            value = 0;
            if (token.Type == JTokenType.Integer)
            {
                value = (long)token;
                return true;
            }

            return token.Type == JTokenType.String && long.TryParse((string)token, out value);
        }

        private static JObject ParseObject(string body)
        {
            try
            {
                return string.IsNullOrEmpty(body) ? null : JObject.Parse(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
