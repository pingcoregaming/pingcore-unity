using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Discovery.Client;
using PingCore.Discovery.Host;
using PingCore.Fleet;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// Reflection over the SDK's wire DTOs: every type carrying <see cref="WireContractAttribute"/>
    /// in the four non-NGO runtime assemblies, and every type reachable from one through its
    /// JSON properties.
    /// </summary>
    internal static class WireCatalog
    {
        /// <summary>The runtime assemblies that may hold wire DTOs.</summary>
        public static IReadOnlyList<Assembly> RuntimeAssemblies { get; } = new[]
        {
            typeof(PingCoreSdkInfo).Assembly,
            typeof(DiscoveryClient).Assembly,
            typeof(HeartbeatReporter).Assembly,
            typeof(FleetSdk).Assembly,
        };

        /// <summary>Types that carry at least one <see cref="WireContractAttribute"/>, sorted by full name.</summary>
        public static IReadOnlyList<Type> ContractTypes()
        {
            return RuntimeAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.GetCustomAttributes<WireContractAttribute>(false).Any())
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Contract types plus every DTO reachable from them, sorted by full name.</summary>
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
                    foreach (Type nested in NestedDtoTypes(property.PropertyType))
                    {
                        queue.Enqueue(nested);
                    }
                }
            }

            return seen.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        }

        /// <summary>Resolves a DTO by CLR full name in the runtime assemblies, or null.</summary>
        public static Type FindType(string fullName)
        {
            return RuntimeAssemblies.Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);
        }

        /// <summary>
        /// Public instance properties that are serialized, in declaration order: base types
        /// first, then each derived type, each in metadata order. <c>[JsonIgnore]</c> properties
        /// are left out.
        /// </summary>
        public static IReadOnlyList<PropertyInfo> JsonProperties(Type type)
        {
            var chain = new List<Type>();
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                chain.Insert(0, t);
            }

            return chain
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .OrderBy(p => p.MetadataToken))
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .ToList();
        }

        /// <summary>The <c>[JsonProperty]</c> on a property, or null.</summary>
        public static JsonPropertyAttribute JsonAttribute(PropertyInfo property) => property.GetCustomAttribute<JsonPropertyAttribute>();

        /// <summary>
        /// The <c>NullValueHandling</c> a property's <c>[JsonProperty]</c> names explicitly, or
        /// null when it names none. The attribute's own getter cannot tell an unset value from
        /// <c>Include</c> (both read as 0), so this reads the attribute's named arguments.
        /// </summary>
        public static NullValueHandling? ExplicitNullValueHandling(PropertyInfo property)
        {
            CustomAttributeData data = property.GetCustomAttributesData()
                .FirstOrDefault(d => d.AttributeType == typeof(JsonPropertyAttribute));
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

        /// <summary>True for a SDK DTO class, as opposed to a primitive, collection or JSON.NET token.</summary>
        public static bool IsDto(Type type)
        {
            return type.IsClass && type != typeof(string) && !typeof(JToken).IsAssignableFrom(type)
                && !typeof(IEnumerable).IsAssignableFrom(type) && RuntimeAssemblies.Contains(type.Assembly);
        }

        /// <summary>Element type of a list or array, or null.</summary>
        public static Type ListElementType(Type type)
        {
            if (type.IsArray)
            {
                return type.GetElementType();
            }

            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>)
                || type.GetGenericTypeDefinition() == typeof(IList<>)
                || type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)))
            {
                return type.GetGenericArguments()[0];
            }

            return null;
        }

        /// <summary>Value type of a string-keyed dictionary, or null.</summary>
        public static Type MapValueType(Type type)
        {
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                || type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                && type.GetGenericArguments()[0] == typeof(string))
            {
                return type.GetGenericArguments()[1];
            }

            return null;
        }

        private static IEnumerable<Type> NestedDtoTypes(Type type)
        {
            Type element = ListElementType(type) ?? MapValueType(type);
            if (element != null)
            {
                type = element;
            }

            if (IsDto(type))
            {
                yield return type;
            }
        }
    }
}
