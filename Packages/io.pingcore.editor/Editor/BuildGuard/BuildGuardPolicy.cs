using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// The build guard's decisions as pure functions: no Unity API and no file system access beyond
    /// path arithmetic. The preprocessor and the postprocessor gather inputs and act on the findings.
    /// Byte scanning is <see cref="BuildGuardByteScanner"/>; output coverage is <see cref="BuildGuardCoverage"/>;
    /// the heartbeat-token exemption is <see cref="BuildGuardConfirmations"/>.
    /// </summary>
    public static class BuildGuardPolicy
    {
        public const string EditorAssemblyPrefix = "PingCore.Editor";

        /// <summary>The token shape: a platform prefix and 16 or more letters or digits, not preceded by a letter or digit.</summary>
        public const string TokenPattern = "(?<![A-Za-z0-9])(usr|sys|cdnpush|dsc)_[A-Za-z0-9]{16,}";

        /// <summary>The only prefix that can be allowed, and only as a confirmed open-registration heartbeat token.</summary>
        public const string DscPrefix = "dsc_";

        public const int MinimumTokenTail = 16;

        internal static readonly string[] TokenPrefixes = { "usr_", "sys_", "cdnpush_", DscPrefix };

        private static readonly Regex TokenRegex = new Regex(TokenPattern, RegexOptions.CultureInvariant);

        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// True only when <paramref name="outputPath"/> resolves strictly inside <c>&lt;projectRoot&gt;/&lt;folder&gt;/</c>
        /// (<paramref name="folder"/> project-relative, forward slashes). False for a null or empty folder. Pure.
        /// </summary>
        public static bool IsInsideProjectFolder(string projectRoot, string outputPath, string folder)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            string root = NormalizeFullPath(projectRoot);
            string output = NormalizeFullPath(Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(projectRoot, outputPath));
            string prefix = NormalizeFullPath(Path.Combine(projectRoot, folder)) + "/";
            return prefix.StartsWith(root + "/", PathComparison) && output.Length > prefix.Length && output.StartsWith(prefix, PathComparison);
        }

        /// <summary>
        /// Rejects <c>PingCore.Editor*</c> always, and an assembly whose name starts with one of the project's restricted
        /// prefixes unless <paramref name="scope"/> is an instrumented output (<see cref="BuildGuardRules"/>). Accepts bare
        /// names, file names (<c>X.dll</c>) and paths.
        /// </summary>
        public static IReadOnlyList<BuildGuardFinding> CheckAssemblies(IEnumerable<string> assemblyNames, BuildGuardScope scope)
        {
            BuildGuardScope s = scope ?? BuildGuardScope.Plain;
            var findings = new List<BuildGuardFinding>();
            if (assemblyNames == null)
            {
                return findings;
            }

            foreach (string raw in assemblyNames)
            {
                string name = ToAssemblyName(raw);
                if (name.Length == 0)
                {
                    continue;
                }

                if (name.StartsWith(EditorAssemblyPrefix, StringComparison.Ordinal))
                {
                    findings.Add(new BuildGuardFinding(BuildGuardReason.EditorAssembly, name,
                        "Editor-only assemblies never ship in a player build"));
                }
                else if (!s.IsInstrumentedOutput && RestrictedPrefixOf(name, s.Rules) != null)
                {
                    findings.Add(new BuildGuardFinding(BuildGuardReason.InstrumentationInPlainBuild, name,
                        "a restricted assembly (prefix " + RestrictedPrefixOf(name, s.Rules) + ", " + BuildGuardRules.RelativePath + ") ships only in an instrumented build"
                        + InstrumentedFolderNote(s.Rules)));
                }
            }

            return findings;
        }

        /// <summary>Rejects one of the project's instrumentation defines unless <paramref name="scope"/> is an instrumented output.</summary>
        public static IReadOnlyList<BuildGuardFinding> CheckDefines(IEnumerable<string> defines, BuildGuardScope scope)
        {
            BuildGuardScope s = scope ?? BuildGuardScope.Plain;
            var findings = new List<BuildGuardFinding>();
            if (defines == null || s.IsInstrumentedOutput)
            {
                return findings;
            }

            foreach (string define in defines)
            {
                string trimmed = define?.Trim();
                if (trimmed != null && Contains(s.Rules.InstrumentationDefines, trimmed))
                {
                    findings.Add(new BuildGuardFinding(BuildGuardReason.InstrumentationInPlainBuild, trimmed,
                        "an instrumentation define (" + BuildGuardRules.RelativePath + ") is allowed only in an instrumented build" + InstrumentedFolderNote(s.Rules)));
                    break;
                }
            }

            return findings;
        }

        private static string RestrictedPrefixOf(string assemblyName, BuildGuardRules rules)
        {
            foreach (string prefix in rules.RestrictedAssemblyPrefixes)
            {
                if (assemblyName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return prefix;
                }
            }

            return null;
        }

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            foreach (string v in values)
            {
                if (string.Equals(v, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string InstrumentedFolderNote(BuildGuardRules rules) =>
            rules.InstrumentedOutputFolder == null ? "; the rules name no instrumented output folder" : ", written under " + rules.InstrumentedOutputFolder + "/";

        /// <summary>Splits a Unity define string (<c>A;B;C</c>) into its symbols.</summary>
        public static IReadOnlyList<string> SplitDefines(string defines)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(defines))
            {
                return result;
            }

            foreach (string part in defines.Split(';', ','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result;
        }

        /// <summary>Scans a string with <see cref="TokenPattern"/>. Positions are reported as line numbers.</summary>
        public static BuildGuardScanResult ScanText(string text, string location, IReadOnlyCollection<string> allowedDscTokens = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return BuildGuardScanResult.Empty;
            }

            var findings = new List<BuildGuardFinding>();
            int allowed = 0;
            foreach (Match match in TokenRegex.Matches(text))
            {
                if (IsAllowedDscToken(match.Value, allowedDscTokens))
                {
                    allowed++;
                    continue;
                }

                findings.Add(SecretFinding(location, match.Value, "line " + LineOf(text, match.Index)));
            }

            return new BuildGuardScanResult(findings, allowed);
        }

        /// <summary>Scans raw bytes as UTF-8 and UTF-16LE (see <see cref="BuildGuardByteScanner"/>).</summary>
        public static BuildGuardScanResult ScanBytes(byte[] data, string location, IReadOnlyCollection<string> allowedDscTokens = null) =>
            BuildGuardByteScanner.Scan(data, location, allowedDscTokens);

        /// <summary>Strips any directory and a trailing <c>.dll</c> from an assembly reference.</summary>
        public static string ToAssemblyName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string name = raw.Trim().Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            return name;
        }

        /// <summary>A full path with forward slashes and no trailing slash.</summary>
        public static string NormalizeFullPath(string path)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            return full.Length > 1 ? full.TrimEnd('/') : full;
        }

        /// <summary>True when <paramref name="path"/> equals <paramref name="directory"/> or lies inside it.</summary>
        public static bool IsSameOrInside(string path, string directory)
        {
            string p = NormalizeFullPath(path);
            string d = NormalizeFullPath(directory);
            return string.Equals(p, d, PathComparison) || p.StartsWith(d + "/", PathComparison);
        }

        /// <summary>
        /// True only for a <c>dsc_</c> token equal to one of <paramref name="allowedDscTokens"/>, which
        /// holds only heartbeat tokens with a matching confirmation (<see cref="BuildGuardConfirmations.Resolve"/>).
        /// </summary>
        internal static bool IsAllowedDscToken(string token, IReadOnlyCollection<string> allowedDscTokens)
        {
            if (allowedDscTokens == null || allowedDscTokens.Count == 0 || !token.StartsWith(DscPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (string allowedToken in allowedDscTokens)
            {
                if (string.Equals(allowedToken, token, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A <c>secret_literal</c> finding that names the prefix, the length and the position, never the token.</summary>
        internal static BuildGuardFinding SecretFinding(string location, string token, string position)
        {
            string prefix = token.Substring(0, token.IndexOf('_') + 1);
            return new BuildGuardFinding(BuildGuardReason.SecretLiteral, location,
                prefix + " token-shaped string (" + token.Length + " chars) at " + position);
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }
    }
}
