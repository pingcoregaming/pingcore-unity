using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// Builds the <c>pingcore-dto-dump/1</c> document a contract checker compares with the pinned
    /// specs. The shape is normative:
    /// <c>{format, types:[{clrType, assembly, contracts[], properties[]}], enums[]}</c>.
    /// </summary>
    internal static class DtoDump
    {
        public const string Format = "pingcore-dto-dump/1";

        public static JObject Build()
        {
            var types = new JArray();
            foreach (Type type in WireCatalog.AllWireTypes())
            {
                types.Add(DescribeType(type));
            }

            var reasonValues = new JArray(DiscoveryReasons.AllWireValues().Cast<object>().ToArray());
            var enums = new JArray
            {
                new JObject
                {
                    ["clrType"] = typeof(DiscoveryReason).FullName,
                    ["role"] = "discovery-reason",
                    ["values"] = reasonValues,
                },
            };

            return new JObject
            {
                ["format"] = Format,
                ["types"] = types,
                ["enums"] = enums,
            };
        }

        private static JObject DescribeType(Type type)
        {
            var contracts = new JArray();
            foreach (WireContractAttribute contract in type.GetCustomAttributes<WireContractAttribute>(false))
            {
                contracts.Add(new JObject
                {
                    ["source"] = contract.Source,
                    ["method"] = contract.Method,
                    ["path"] = contract.Path,
                    ["direction"] = DirectionName(contract.Direction),
                    // A request body has no status; the attribute and the fixtures both say 0.
                    ["status"] = contract.Status,
                });
            }

            var properties = new JArray();
            foreach (PropertyInfo property in WireCatalog.JsonProperties(type))
            {
                properties.Add(DescribeProperty(property));
            }

            return new JObject
            {
                ["clrType"] = type.FullName,
                ["assembly"] = type.Assembly.GetName().Name,
                ["contracts"] = contracts,
                ["properties"] = properties,
            };
        }

        private static JObject DescribeProperty(PropertyInfo property)
        {
            JsonPropertyAttribute json = WireCatalog.JsonAttribute(property);
            Type clr = property.PropertyType;
            Type underlying = Nullable.GetUnderlyingType(clr);
            (string kind, string reference, JToken items) = Shape(underlying ?? clr);

            Required required = json?.Required ?? Required.Default;
            bool explicitNull = WireCatalog.ExplicitNullValueHandling(property) == NullValueHandling.Include;
            // Nullable means the wire may carry an explicit null: the property says so with
            // Required.AllowNull or NullValueHandling.Include. An optional property that is
            // simply left out when unset (NullValueHandling.Ignore) is not nullable.
            bool nullable = required == Required.AllowNull || explicitNull || kind == "any";

            return new JObject
            {
                ["json"] = json?.PropertyName,
                ["clr"] = property.Name,
                ["type"] = kind,
                ["ref"] = reference == null ? JValue.CreateNull() : new JValue(reference),
                ["items"] = items ?? JValue.CreateNull(),
                ["nullable"] = nullable,
                ["required"] = required == Required.Always || required == Required.AllowNull,
            };
        }

        /// <summary>Maps a CLR type to the dump's JSON type vocabulary.</summary>
        public static (string kind, string reference, JToken items) Shape(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string))
            {
                return ("string", null, null);
            }

            if (underlying == typeof(bool))
            {
                return ("boolean", null, null);
            }

            if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) || underlying == typeof(byte)
                || underlying == typeof(uint) || underlying == typeof(ulong))
            {
                return ("integer", null, null);
            }

            if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
            {
                return ("number", null, null);
            }

            if (underlying == typeof(JObject))
            {
                return ("object", null, null);
            }

            if (underlying == typeof(JArray))
            {
                return ("array", null, new JObject { ["type"] = "any", ["ref"] = JValue.CreateNull() });
            }

            if (typeof(JToken).IsAssignableFrom(underlying))
            {
                return ("any", null, null);
            }

            Type element = WireCatalog.ListElementType(underlying);
            if (element != null)
            {
                return ("array", null, ItemShape(element));
            }

            Type mapValue = WireCatalog.MapValueType(underlying);
            if (mapValue != null)
            {
                return ("map", null, ItemShape(mapValue));
            }

            if (WireCatalog.IsDto(underlying))
            {
                return ("object", underlying.FullName, null);
            }

            throw new NotSupportedException($"No dump mapping for CLR type {underlying.FullName}.");
        }

        private static JObject ItemShape(Type element)
        {
            (string kind, string reference, JToken _) = Shape(element);
            return new JObject
            {
                ["type"] = kind,
                ["ref"] = reference == null ? JValue.CreateNull() : new JValue(reference),
            };
        }

        public static string DirectionName(WireDirection direction)
        {
            switch (direction)
            {
                case WireDirection.Request:
                    return "request";
                case WireDirection.Response:
                    return "response";
                case WireDirection.Error:
                    return "error";
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }
        }
    }
}
