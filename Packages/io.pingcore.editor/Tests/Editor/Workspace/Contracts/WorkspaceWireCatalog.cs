using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Editor.Workspace.Api;

namespace PingCore.Editor.Workspace.Tests.Contracts
{
    /// <summary>
    /// Reflection over the Editor plugin's PingCore API DTOs (<c>PingCore.Editor.Workspace</c>):
    /// every type carrying <see cref="WireContractAttribute"/> and every type reachable from one,
    /// and the <c>pingcore-dto-dump/1</c> document a contract checker reads from
    /// <c>Library/pingcore-editor-dto-dump.json</c>. The shape is the SDK dump's, with <c>enums</c>
    /// empty and a <c>routes</c> table added (<see cref="WorkspaceRoutes.All"/>, method and path only).
    /// </summary>
    internal static class WorkspaceWireCatalog
    {
        /// <summary>The dump's format marker.</summary>
        public const string Format = "pingcore-dto-dump/1";

        /// <summary>The assembly that holds the DTOs.</summary>
        public static Assembly WorkspaceAssembly => typeof(IPingCoreApi).Assembly;

        public static IReadOnlyList<Type> ContractTypes()
        {
            return WorkspaceAssembly.GetTypes()
                .Where(t => t.GetCustomAttributes<WireContractAttribute>(false).Any())
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
        }

        public static IReadOnlyList<Type> AllWireTypes()
        {
            var seen = new HashSet<Type>();
            var queue = new Queue<Type>(ContractTypes());
            while (queue.Count > 0)
            {
                Type type = queue.Dequeue();
                if (!seen.Add(type))
                {
                    continue;
                }

                foreach (PropertyInfo property in JsonProperties(type))
                {
                    Type nested = ListElementType(property.PropertyType) ?? MapValueType(property.PropertyType) ?? property.PropertyType;
                    if (IsDto(nested))
                    {
                        queue.Enqueue(nested);
                    }
                }
            }

            return seen.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        }

        public static Type FindType(string fullName) => WorkspaceAssembly.GetType(fullName ?? string.Empty, false);

        /// <summary>Serialized public instance properties in declaration order, base types first; <c>[JsonIgnore]</c> left out.</summary>
        public static IReadOnlyList<PropertyInfo> JsonProperties(Type type)
        {
            var chain = new List<Type>();
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                chain.Insert(0, t);
            }

            return chain
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(p => p.MetadataToken))
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .ToList();
        }

        public static bool IsDto(Type type)
        {
            return type.IsClass && type != typeof(string) && !typeof(JToken).IsAssignableFrom(type)
                && !typeof(IEnumerable).IsAssignableFrom(type) && type.Assembly == WorkspaceAssembly;
        }

        public static NullValueHandling? ExplicitNullValueHandling(PropertyInfo property)
        {
            CustomAttributeData data = property.GetCustomAttributesData().FirstOrDefault(d => d.AttributeType == typeof(JsonPropertyAttribute));
            if (data == null)
            {
                return null;
            }

            foreach (CustomAttributeNamedArgument argument in data.NamedArguments)
            {
                if (argument.MemberName == nameof(JsonPropertyAttribute.NullValueHandling))
                {
                    return (NullValueHandling)(int)argument.TypedValue.Value;
                }
            }

            return null;
        }

        /// <summary>The dump document.</summary>
        public static JObject BuildDump()
        {
            var types = new JArray();
            foreach (Type type in AllWireTypes())
            {
                var contracts = new JArray();
                foreach (WireContractAttribute c in type.GetCustomAttributes<WireContractAttribute>(false))
                {
                    contracts.Add(new JObject
                    {
                        ["source"] = c.Source,
                        ["method"] = c.Method,
                        ["path"] = c.Path,
                        ["direction"] = DirectionName(c.Direction),
                        ["status"] = c.Status,
                    });
                }

                var properties = new JArray();
                foreach (PropertyInfo property in JsonProperties(type))
                {
                    properties.Add(DescribeProperty(property));
                }

                types.Add(new JObject
                {
                    ["clrType"] = type.FullName,
                    ["assembly"] = type.Assembly.GetName().Name,
                    ["contracts"] = contracts,
                    ["properties"] = properties,
                });
            }

            // The route table (method and path, step, caller), which the contract checker compares with the API contract
            // snapshots (not published); the plugin itself ships no route file or pattern.
            var routes = new JArray();
            foreach (WorkspaceRoute route in WorkspaceRoutes.All)
            {
                routes.Add(new JObject
                {
                    ["id"] = route.Id.ToString(),
                    ["method"] = route.Method,
                    ["template"] = route.Template,
                    ["step"] = route.Step,
                    ["caller"] = route.Caller.ToString(),
                });
            }

            return new JObject { ["format"] = Format, ["types"] = types, ["enums"] = new JArray(), ["routes"] = routes };
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

        private static JObject DescribeProperty(PropertyInfo property)
        {
            JsonPropertyAttribute json = property.GetCustomAttribute<JsonPropertyAttribute>();
            (string kind, string reference, JToken items) = Shape(property.PropertyType);
            Required required = json?.Required ?? Required.Default;
            bool nullable = required == Required.AllowNull || ExplicitNullValueHandling(property) == NullValueHandling.Include || kind == "any";
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

        private static (string kind, string reference, JToken items) Shape(Type type)
        {
            Type t = Nullable.GetUnderlyingType(type) ?? type;
            if (t == typeof(string))
            {
                return ("string", null, null);
            }

            if (t == typeof(bool))
            {
                return ("boolean", null, null);
            }

            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong))
            {
                return ("integer", null, null);
            }

            if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            {
                return ("number", null, null);
            }

            if (t == typeof(JObject))
            {
                return ("object", null, null);
            }

            if (t == typeof(JArray))
            {
                return ("array", null, new JObject { ["type"] = "any", ["ref"] = JValue.CreateNull() });
            }

            if (typeof(JToken).IsAssignableFrom(t))
            {
                return ("any", null, null);
            }

            Type element = ListElementType(t);
            if (element != null)
            {
                return ("array", null, ItemShape(element));
            }

            Type value = MapValueType(t);
            if (value != null)
            {
                return ("map", null, ItemShape(value));
            }

            if (IsDto(t))
            {
                return ("object", t.FullName, null);
            }

            throw new NotSupportedException($"No dump mapping for CLR type {t.FullName}.");
        }

        private static JObject ItemShape(Type element)
        {
            (string kind, string reference, JToken _) = Shape(element);
            return new JObject { ["type"] = kind, ["ref"] = reference == null ? JValue.CreateNull() : new JValue(reference) };
        }

        private static Type ListElementType(Type type)
        {
            if (type.IsArray)
            {
                return type.GetElementType();
            }

            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(IList<>) || type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)))
            {
                return type.GetGenericArguments()[0];
            }

            return null;
        }

        private static Type MapValueType(Type type)
        {
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) || type.GetGenericTypeDefinition() == typeof(IDictionary<,>) || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                && type.GetGenericArguments()[0] == typeof(string))
            {
                return type.GetGenericArguments()[1];
            }

            return null;
        }
    }
}
