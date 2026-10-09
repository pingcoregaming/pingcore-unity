using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// <see cref="DiscoveryReasons"/> holds a literal string-to-enum table, because Core does no
    /// runtime reflection. These tests read the enum's <c>[EnumMember]</c> attributes (reflection
    /// is fine in an Editor test) and fail when the literal table disagrees with them in either
    /// direction. The table reaches the spec through the DTO dump: <c>DtoDump</c> writes
    /// <see cref="DiscoveryReasons.AllWireValues"/> as the <c>discovery-reason</c> enum, and
    /// a contract checker compares the dump with the pinned specs and fails on a spec reason it lacks.
    /// </summary>
    public sealed class DiscoveryReasonTableTests
    {
        [Test]
        public void TheLiteralTableMatchesTheEnumMemberAttributesInBothDirections()
        {
            IReadOnlyList<KeyValuePair<string, DiscoveryReason>> attributes = AttributeTable();
            Assert.That(attributes.Count, Is.EqualTo(Enum.GetValues(typeof(DiscoveryReason)).Length - 1), "every member but Unknown carries [EnumMember]");

            List<string> drift = Drift(attributes, DiscoveryReasons.AllWireValues(), DiscoveryReasons.Parse, DiscoveryReasons.ToWireValue);
            Assert.That(drift, Is.Empty, "DiscoveryReasons' literal table disagrees with DiscoveryReason's [EnumMember] attributes:\n" + string.Join("\n", drift));
        }

        [Test]
        public void TheDriftCheckFlagsADroppedAddedOrMiswiredEntry()
        {
            IReadOnlyList<KeyValuePair<string, DiscoveryReason>> attributes = AttributeTable();
            List<string> values = DiscoveryReasons.AllWireValues().ToList();
            Func<string, DiscoveryReason> parse = DiscoveryReasons.Parse;
            Func<DiscoveryReason, string> toWire = DiscoveryReasons.ToWireValue;
            Assert.That(Drift(attributes, values, parse, toWire), Is.Empty, "precondition: the real table is clean");

            List<string> dropped = values.Where(v => v != "no_seats").ToList();
            Assert.That(Drift(attributes, dropped, parse, toWire), Has.Some.Contains("\"no_seats\" is missing from the table"));

            List<string> added = values.Concat(new[] { "made_up_reason" }).ToList();
            Assert.That(Drift(attributes, added, parse, toWire), Has.Some.Contains("\"made_up_reason\" has no [EnumMember]"));

            Func<string, DiscoveryReason> miswiredParse = v => v == "no_seats" ? DiscoveryReason.WrongServer : DiscoveryReasons.Parse(v);
            Assert.That(Drift(attributes, values, miswiredParse, toWire), Has.Some.Contains("Parse(\"no_seats\") gives WrongServer"));

            Func<DiscoveryReason, string> miswiredToWire = r => r == DiscoveryReason.NoSeats ? "no_seat" : DiscoveryReasons.ToWireValue(r);
            Assert.That(Drift(attributes, values, parse, miswiredToWire), Has.Some.Contains("ToWireValue(NoSeats) gives \"no_seat\""));

            Func<DiscoveryReason, string> unknownHasWire = r => r == DiscoveryReason.Unknown ? "unknown" : DiscoveryReasons.ToWireValue(r);
            Assert.That(Drift(attributes, values, parse, unknownHasWire), Has.Some.Contains("ToWireValue(Unknown)"));

            List<string> reordered = values.Skip(1).Concat(values.Take(1)).ToList();
            Assert.That(Drift(attributes, reordered, parse, toWire), Has.Some.Contains("declaration order"));

            List<string> doubled = values.Concat(new[] { "no_seats" }).ToList();
            Assert.That(Drift(attributes, doubled, parse, toWire), Has.Some.Contains("\"no_seats\" is listed twice"));
        }

        [Test]
        public void TheDumpCarriesTheLiteralTableToCheckContracts()
        {
            JObject dump = DtoDump.Build();
            JObject reasons = ((JArray)dump["enums"]).Cast<JObject>().Single(e => (string)e["role"] == "discovery-reason");
            List<string> dumped = ((JArray)reasons["values"]).Select(v => (string)v).ToList();
            Assert.That(dumped, Is.EqualTo(DiscoveryReasons.AllWireValues()), "the dump writes the literal table");
            Assert.That(dumped, Is.EqualTo(AttributeTable().Select(e => e.Key).ToList()), "and so the [EnumMember] values");
        }

        /// <summary>The <c>[EnumMember]</c> values of <see cref="DiscoveryReason"/>, in declaration order.</summary>
        internal static IReadOnlyList<KeyValuePair<string, DiscoveryReason>> AttributeTable()
        {
            return typeof(DiscoveryReason).GetFields(BindingFlags.Public | BindingFlags.Static)
                .OrderBy(f => f.MetadataToken)
                .Select(f => (Field: f, Member: f.GetCustomAttribute<EnumMemberAttribute>()))
                .Where(x => x.Member != null)
                .Select(x => new KeyValuePair<string, DiscoveryReason>(x.Member.Value, (DiscoveryReason)x.Field.GetValue(null)))
                .ToList();
        }

        /// <summary>Every disagreement between the attribute table and a table view (its values, Parse and ToWireValue).</summary>
        internal static List<string> Drift(
            IReadOnlyList<KeyValuePair<string, DiscoveryReason>> attributes,
            IReadOnlyList<string> tableValues,
            Func<string, DiscoveryReason> parse,
            Func<DiscoveryReason, string> toWire)
        {
            var problems = new List<string>();
            var attributeValues = new HashSet<string>(attributes.Select(a => a.Key), StringComparer.Ordinal);
            var table = new HashSet<string>(tableValues, StringComparer.Ordinal);
            foreach (KeyValuePair<string, DiscoveryReason> entry in attributes)
            {
                if (!table.Contains(entry.Key))
                {
                    problems.Add($"\"{entry.Key}\" is missing from the table ([EnumMember] on {entry.Value})");
                }

                DiscoveryReason parsed = parse(entry.Key);
                if (parsed != entry.Value)
                {
                    problems.Add($"Parse(\"{entry.Key}\") gives {parsed}, [EnumMember] says {entry.Value}");
                }

                string wire = toWire(entry.Value);
                if (!string.Equals(wire, entry.Key, StringComparison.Ordinal))
                {
                    problems.Add($"ToWireValue({entry.Value}) gives {(wire == null ? "null" : $"\"{wire}\"")}, [EnumMember] says \"{entry.Key}\"");
                }
            }

            foreach (string value in tableValues.Where(v => !attributeValues.Contains(v)))
            {
                problems.Add($"\"{value}\" has no [EnumMember] on DiscoveryReason");
            }

            foreach (string value in tableValues.GroupBy(v => v, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            {
                problems.Add($"\"{value}\" is listed twice in the table");
            }

            if (toWire(DiscoveryReason.Unknown) != null)
            {
                problems.Add("ToWireValue(Unknown) must be null");
            }

            if (problems.Count == 0 && !tableValues.SequenceEqual(attributes.Select(a => a.Key), StringComparer.Ordinal))
            {
                problems.Add("the table is not in enum declaration order");
            }

            return problems;
        }
    }
}
