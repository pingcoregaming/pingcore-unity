using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// The project's own build guard rules, read from <c>ProjectSettings/PingCoreBuildGuardRules.json</c> when it exists.
    /// They name the instrumentation a project keeps out of its plain builds: an automation harness, debug tools, anything
    /// that must never reach players.
    /// <list type="bullet">
    /// <item><c>restrictedAssemblyPrefixes</c>: an assembly whose name starts with one of these never ships in a plain build.</item>
    /// <item><c>instrumentationDefines</c>: a build with one of these scripting defines is an instrumented build.</item>
    /// <item><c>instrumentedOutputFolder</c> (optional): a project-relative folder; a build that writes strictly inside it is
    /// an instrumented build and may carry both. Empty refuses them in every build.</item>
    /// </list>
    /// Anything else in the file, a key of the wrong type, a malformed prefix, define or folder is a problem, and the guard
    /// fails the build closed rather than guess (<see cref="BuildGuardReason.ScanError"/>, whose detail is the
    /// <see cref="BuildGuardRulesException"/> message: the file and each problem in plain words, quoting nothing from the
    /// file but a key name). With no file there are no rules. Pure apart from <see cref="Read"/>.
    /// </summary>
    public sealed class BuildGuardRules
    {
        /// <summary>The rules file, relative to the project.</summary>
        public const string RelativePath = "ProjectSettings/PingCoreBuildGuardRules.json";

        public const string RestrictedAssemblyPrefixesKey = "restrictedAssemblyPrefixes";
        public const string InstrumentationDefinesKey = "instrumentationDefines";
        public const string InstrumentedOutputFolderKey = "instrumentedOutputFolder";

        private static readonly Regex DefinePattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
        private static readonly Regex PrefixPattern = new Regex("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
        private static readonly Regex SegmentPattern = new Regex("^[A-Za-z0-9_][A-Za-z0-9 ._-]*$", RegexOptions.CultureInvariant);
        private static readonly string[] ReservedTopFolders = { "Assets", "Packages", "ProjectSettings", "Library", "UserSettings", "Temp", "Logs" };

        // A key name is quoted in a problem only when it is a short plain identifier (a letter, then up to 39 letters, digits
        // or underscores), holds no run of 12 or more hex digits (a CDN token, a game server key or a digest pasted as a
        // key) and is not token-shaped; anything else is described.
        private static readonly Regex ShowableKey = new Regex("^[A-Za-z][A-Za-z0-9_]{0,39}$", RegexOptions.CultureInvariant);
        private static readonly Regex HexRun = new Regex("[0-9A-Fa-f]{12,}", RegexOptions.CultureInvariant);
        private static readonly Regex TokenShape = new Regex(BuildGuardPolicy.TokenPattern, RegexOptions.CultureInvariant);

        public BuildGuardRules(IEnumerable<string> restrictedAssemblyPrefixes, IEnumerable<string> instrumentationDefines, string instrumentedOutputFolder)
        {
            RestrictedAssemblyPrefixes = new List<string>(restrictedAssemblyPrefixes ?? Array.Empty<string>()).AsReadOnly();
            InstrumentationDefines = new List<string>(instrumentationDefines ?? Array.Empty<string>()).AsReadOnly();
            InstrumentedOutputFolder = string.IsNullOrEmpty(instrumentedOutputFolder) ? null : instrumentedOutputFolder;
        }

        /// <summary>No rules: nothing restricted, no instrumented builds.</summary>
        public static BuildGuardRules None { get; } = new BuildGuardRules(null, null, null);

        /// <summary>Assembly name prefixes that never ship in a plain build.</summary>
        public IReadOnlyList<string> RestrictedAssemblyPrefixes { get; }

        /// <summary>Scripting defines that make a build instrumented.</summary>
        public IReadOnlyList<string> InstrumentationDefines { get; }

        /// <summary>The project-relative folder an instrumented build writes inside, forward slashes; null when none is allowed.</summary>
        public string InstrumentedOutputFolder { get; }

        /// <summary>True when the rules restrict nothing.</summary>
        public bool IsEmpty => RestrictedAssemblyPrefixes.Count == 0 && InstrumentationDefines.Count == 0;

        /// <summary>
        /// Parses the rules file's text. Throws <see cref="BuildGuardRulesException"/> naming the file and every problem in
        /// plain words (unknown key, duplicate key, a key that must be a list of strings, trailing content after the object,
        /// not valid JSON at a line and position), never a value from the file but a key name, so a typo can never quietly
        /// turn a rule off and the build log never quotes the file.
        /// </summary>
        public static BuildGuardRules Parse(string json)
        {
            var problems = new List<string>();
            JObject root = ReadObject(json ?? string.Empty, problems);
            if (root == null)
            {
                throw new BuildGuardRulesException(RelativePath + ": " + string.Join("; ", problems));
            }

            foreach (JProperty property in root.Properties())
            {
                if (property.Name != RestrictedAssemblyPrefixesKey && property.Name != InstrumentationDefinesKey && property.Name != InstrumentedOutputFolderKey)
                {
                    problems.Add("unknown key " + KeyName(property.Name));
                }
            }

            IReadOnlyList<string> prefixes = ReadStrings(root, RestrictedAssemblyPrefixesKey, PrefixPattern, "an assembly name prefix", problems);
            IReadOnlyList<string> defines = ReadStrings(root, InstrumentationDefinesKey, DefinePattern, "a scripting define", problems);
            string folder = null;
            JToken folderToken = root[InstrumentedOutputFolderKey];
            if (folderToken != null && folderToken.Type != JTokenType.Null)
            {
                if (folderToken.Type != JTokenType.String)
                {
                    problems.Add(InstrumentedOutputFolderKey + " must be a string");
                }
                else
                {
                    folder = (string)folderToken;
                    string folderProblem = OutputFolderProblem(folder);
                    if (folder.Length > 0 && folderProblem != null)
                    {
                        problems.Add(folderProblem);
                    }
                }
            }

            if (problems.Count > 0)
            {
                throw new BuildGuardRulesException(RelativePath + ": " + string.Join("; ", problems));
            }

            return new BuildGuardRules(prefixes, defines, folder);
        }

        /// <summary>
        /// The file's one top-level object, or null with the reason in <paramref name="problems"/>. A key given twice is a
        /// problem, never "the last one wins" (a second, empty list must not switch a rule off); comments are allowed
        /// anywhere; anything after the object is a problem. Positions are 1-based lines and columns.
        /// </summary>
        private static JObject ReadObject(string json, List<string> problems)
        {
            var root = new JObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                try
                {
                    if (!Next(reader))
                    {
                        problems.Add("the file is empty; it must hold one JSON object");
                        return null;
                    }

                    if (reader.TokenType != JsonToken.StartObject)
                    {
                        problems.Add("the file must hold one JSON object, and it starts with " + Describe(reader.TokenType));
                        return null;
                    }

                    while (true)
                    {
                        if (!Next(reader))
                        {
                            problems.Add("not valid JSON at line " + reader.LineNumber + ", position " + reader.LinePosition + " (the object is not closed)");
                            return null;
                        }

                        if (reader.TokenType == JsonToken.EndObject)
                        {
                            break;
                        }

                        string name = (string)reader.Value;
                        if (!Next(reader))
                        {
                            problems.Add("not valid JSON at line " + reader.LineNumber + ", position " + reader.LinePosition + " (a key has no value)");
                            return null;
                        }

                        JToken value = JToken.ReadFrom(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                        if (!seen.Add(name))
                        {
                            problems.Add("duplicate key " + KeyName(name));
                        }
                        else
                        {
                            root.Add(name, value);
                        }
                    }
                }
                catch (JsonReaderException e)
                {
                    problems.Add("not valid JSON at line " + e.LineNumber + ", position " + e.LinePosition);
                    return null;
                }
                catch (JsonException)
                {
                    problems.Add("not valid JSON at line " + reader.LineNumber + ", position " + reader.LinePosition);
                    return null;
                }

                try
                {
                    if (Next(reader))
                    {
                        problems.Add("trailing content after the object");
                    }
                }
                catch (JsonException)
                {
                    problems.Add("trailing content after the object");
                }
            }

            return problems.Count == 0 ? root : null;
        }

        /// <summary>Reads the next token that is not a comment; false at the end.</summary>
        private static bool Next(JsonTextReader reader)
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Describe(JsonToken token)
        {
            switch (token)
            {
                case JsonToken.StartArray:
                    return "a list";
                case JsonToken.String:
                    return "a string";
                case JsonToken.Integer:
                case JsonToken.Float:
                    return "a number";
                case JsonToken.Boolean:
                    return "true or false";
                case JsonToken.Null:
                    return "null";
                default:
                    return "something else";
            }
        }

        /// <summary>A key name as a problem quotes it: in quotes when short, plain and not token-shaped, else described.</summary>
        internal static string KeyName(string name)
        {
            return name != null && ShowableKey.IsMatch(name) && !HexRun.IsMatch(name) && !TokenShape.IsMatch(name)
                ? "\"" + name + "\""
                : "(a key name that is not shown: not a short plain identifier, or shaped like a credential)";
        }

        /// <summary>Why <paramref name="folder"/> cannot be the instrumented output folder, or null. Pure.</summary>
        public static string OutputFolderProblem(string folder)
        {
            const string Rule = " must be a folder of the project, forward slashes, no . or .. part, outside Assets, Packages, ProjectSettings, Library, UserSettings, Temp and Logs";
            if (string.IsNullOrEmpty(folder) || folder.Contains("\\") || folder.StartsWith("/", StringComparison.Ordinal) || folder.EndsWith("/", StringComparison.Ordinal) || folder.Contains(":"))
            {
                return InstrumentedOutputFolderKey + Rule;
            }

            string[] segments = folder.Split('/');
            foreach (string segment in segments)
            {
                if (segment == "." || segment == ".." || !SegmentPattern.IsMatch(segment))
                {
                    return InstrumentedOutputFolderKey + Rule;
                }
            }

            foreach (string reserved in ReservedTopFolders)
            {
                if (string.Equals(segments[0], reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return InstrumentedOutputFolderKey + Rule;
                }
            }

            return null;
        }

        /// <summary>The rules of the project at <paramref name="projectRoot"/>: <see cref="None"/> without a file; throws for a file it cannot read or parse.</summary>
        public static BuildGuardRules Read(string projectRoot)
        {
            string path = Path.Combine(projectRoot, RelativePath);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : None;
        }

        private static IReadOnlyList<string> ReadStrings(JObject root, string key, Regex pattern, string what, List<string> problems)
        {
            var values = new List<string>();
            JToken token = root[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return values;
            }

            if (!(token is JArray array))
            {
                problems.Add(key + " must be a list of strings");
                return values;
            }

            foreach (JToken item in array)
            {
                string value = item.Type == JTokenType.String ? (string)item : null;
                if (value == null || !pattern.IsMatch(value))
                {
                    problems.Add(key + " holds an entry that is not " + what);
                    continue;
                }

                if (!values.Contains(value))
                {
                    values.Add(value);
                }
            }

            return values;
        }
    }

    /// <summary>
    /// The rules as they apply to one build: the project's <see cref="BuildGuardRules"/> and whether this build's output lies
    /// strictly inside their instrumented output folder.
    /// </summary>
    public sealed class BuildGuardScope
    {
        public BuildGuardScope(BuildGuardRules rules, bool isInstrumentedOutput)
        {
            Rules = rules ?? BuildGuardRules.None;
            IsInstrumentedOutput = isInstrumentedOutput && Rules.InstrumentedOutputFolder != null;
        }

        /// <summary>No rules, a plain build: only the guard's built-in checks apply.</summary>
        public static BuildGuardScope Plain { get; } = new BuildGuardScope(BuildGuardRules.None, false);

        public BuildGuardRules Rules { get; }

        /// <summary>True only when the rules name an instrumented output folder and the build writes strictly inside it.</summary>
        public bool IsInstrumentedOutput { get; }

        /// <summary>The scope of a build writing <paramref name="outputPath"/> in the project at <paramref name="projectRoot"/>. Pure.</summary>
        public static BuildGuardScope For(BuildGuardRules rules, string projectRoot, string outputPath)
        {
            BuildGuardRules r = rules ?? BuildGuardRules.None;
            return new BuildGuardScope(r, BuildGuardPolicy.IsInsideProjectFolder(projectRoot, outputPath, r.InstrumentedOutputFolder));
        }
    }
}
