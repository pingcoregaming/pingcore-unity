using System;
using System.Collections.Generic;
using System.Linq;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// The game's startup command as the Push check reads it, pure. The supervisor starts a game server with
    /// the active command-line config's process name followed by its rendered arguments
    /// (<c>processName + ' ' + args</c>), run inside the build's folder, so
    /// the first word of the process name is the file the build must hold. It may be quoted (<c>"./My Game.x86_64"</c>),
    /// carry arguments of its own, and start with <c>./</c>. An absolute path, a backslash or a <c>..</c> part is
    /// refused: the plugin checks only a path inside the build. The plugin never guesses a startup command; the
    /// game's setup in the panel or MCP owns it. It is never read from a branch's default deployment spec: that is a
    /// legacy setting (everything runs through deployments, and many deployments can use one branch). A fleet with deployments reads it from each member's spec's template set
    /// (<see cref="ForDeployments"/>, the build must hold every file); a fleet with none from the game's template sets
    /// (<see cref="ForTemplateSets"/>, the build must hold one of them).
    /// </summary>
    public static class StartupCommand
    {
        /// <summary>The hint every startup command problem ends with.</summary>
        public const string FixInPanel = "Set the game's startup command in the panel (its template set's command-line config, the process name).";

        /// <summary>The file <paramref name="processName"/> launches, or null with <paramref name="problem"/> saying why not.</summary>
        public static StartupExecutable Parse(string processName, out string problem)
        {
            problem = null;
            string text = (processName ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                problem = "The game has no startup command: its command-line config names no process. " + FixInPanel;
                return null;
            }

            string token;
            char first = text[0];
            if (first == '"' || first == '\'')
            {
                int close = text.IndexOf(first, 1);
                if (close < 0)
                {
                    problem = $"The game's startup command has an unbalanced quote ({text}). " + FixInPanel;
                    return null;
                }

                token = text.Substring(1, close - 1);
            }
            else
            {
                int space = text.IndexOfAny(new[] { ' ', '\t' });
                token = space < 0 ? text : text.Substring(0, space);
            }

            if (token.Length == 0)
            {
                problem = "The game's startup command starts with an empty name. " + FixInPanel;
                return null;
            }

            string relative = token;
            while (relative.StartsWith("./", StringComparison.Ordinal))
            {
                relative = relative.Substring(2);
            }

            if (token.StartsWith("/", StringComparison.Ordinal))
            {
                problem = $"Your game launches {token}, a path outside the build. The plugin checks only a file inside the build, such as ./MyGame.x86_64. " + FixInPanel;
                return null;
            }

            string[] segments = relative.Split('/');
            if (relative.Length == 0 || relative.Contains("\\") || segments.Any(s => s.Length == 0 || s == "." || s == ".."))
            {
                problem = $"Your game launches {token}, which is not a file inside the build. " + FixInPanel;
                return null;
            }

            return new StartupExecutable(token, relative);
        }

        /// <summary>
        /// The check for a fleet with deployments: every member runs the build Push uploads, so the build must hold the
        /// file each member's template set launches (one file when they agree; each of them, named, when they do not). A
        /// member whose template set cannot be read or names no process is left out and named in the note; when no member
        /// names one the check is SKIPPED, never refused, with each member's reason. A process name the plugin cannot check
        /// (outside the build) or a template set whose active command lines launch different files refuse, naming the
        /// member.
        /// </summary>
        public static StartupCheck ForDeployments(IReadOnlyList<StartupSource> members)
        {
            if (members == null || members.Count == 0)
            {
                return StartupCheck.Skip("the fleet has no deployment to read it from.");
            }

            var found = new List<(StartupExecutable Exe, string Source)>();
            var unresolved = new List<string>();
            foreach (StartupSource member in members.Where(m => m != null))
            {
                (StartupExecutable exe, string skip, string problem) = FromSet(member);
                if (problem != null)
                {
                    return StartupCheck.Refused(Capitalized(member.Label) + ": " + problem);
                }

                if (exe == null)
                {
                    unresolved.Add(member.Label + ": " + skip);
                    continue;
                }

                found.Add((exe, member.Label));
            }

            if (found.Count == 0)
            {
                return StartupCheck.Skip($"no deployment of the fleet names a process to launch ({string.Join("; ", unresolved)}).");
            }

            return StartupCheck.Found(Group(found), false, unresolved.Count == 0 ? null : StartupCheck.NotCheckedPrefix + string.Join("; ", unresolved) + ".");
        }

        /// <summary>
        /// The check for a fleet with no deployment: nothing says which of the game's template sets a deployment will use,
        /// so the build passes when it holds the file ANY of them launches. A set that cannot be read, names no process or
        /// names one the plugin cannot check is left out and named in the note; with no set at all, or none that names a
        /// process, the check is SKIPPED with the reason; when every process name is one the plugin cannot check, it refuses.
        /// </summary>
        public static StartupCheck ForTemplateSets(IReadOnlyList<StartupSource> sets)
        {
            if (sets == null || sets.Count == 0)
            {
                return StartupCheck.Skip("the fleet has no deployment and the game has no template set, so the file your game launches is not known.");
            }

            var found = new List<(StartupExecutable Exe, string Source)>();
            var unresolved = new List<string>();
            var problems = new List<string>();
            foreach (StartupSource set in sets.Where(s => s != null))
            {
                (StartupExecutable exe, string skip, string problem) = FromSet(set);
                if (problem != null)
                {
                    problems.Add(Capitalized(set.Label) + ": " + problem);
                    unresolved.Add(set.Label + "'s process name cannot be checked (" + problem.Replace(" " + FixInPanel, string.Empty).TrimEnd('.') + ")");
                }
                else if (exe == null)
                {
                    // The reason already names the set ("template set Default names no process ...").
                    unresolved.Add(skip);
                }
                else
                {
                    found.Add((exe, set.Label));
                }
            }

            if (found.Count == 0)
            {
                return problems.Count > 0
                    ? StartupCheck.Refused(string.Join(" ", problems))
                    : StartupCheck.Skip($"the fleet has no deployment and no template set of the game names a process to launch ({string.Join("; ", unresolved)}).");
            }

            return StartupCheck.Found(Group(found), true, unresolved.Count == 0 ? null : StartupCheck.NotCheckedPrefix + string.Join("; ", unresolved) + ".");
        }

        /// <summary>
        /// The one file a source's template set launches: its active command-line configs' process names. Null with a skip
        /// (no set, or no process name) or with a problem (a name the plugin cannot check, or names that launch different
        /// files).
        /// </summary>
        private static (StartupExecutable Exe, string Skip, string Problem) FromSet(StartupSource source)
        {
            Api.Wire.TemplateSetResponse set = source.TemplateSet;
            if (set == null)
            {
                return (null, string.IsNullOrWhiteSpace(source.Unresolved) ? "its template set was not read" : source.Unresolved, null);
            }

            string setName = string.IsNullOrWhiteSpace(set.SetName) ? "#" + set.TemplateSetId : set.SetName.Trim();
            List<string> processNames = (set.Configs ?? new List<Api.Wire.TemplateConfigView>())
                .Where(c => c != null && c.Active && string.Equals(c.TemplateType, "cli", StringComparison.Ordinal))
                .Select(c => (c.ProcessName ?? string.Empty).Trim())
                .Where(p => p.Length > 0)
                .ToList();
            if (processNames.Count == 0)
            {
                return (null, $"template set {setName} names no process to launch (no active command-line config with a process name)", null);
            }

            var parsed = new List<StartupExecutable>();
            foreach (string processName in processNames)
            {
                StartupExecutable exe = Parse(processName, out string problem);
                if (exe == null)
                {
                    return (null, null, problem);
                }

                parsed.Add(exe);
            }

            List<StartupExecutable> distinct = parsed.GroupBy(e => e.RelativePath, StringComparer.Ordinal).Select(g => g.First()).ToList();
            if (distinct.Count > 1)
            {
                return (null, null, $"Template set {setName} has active command-line configs that launch different files ({string.Join(", ", distinct.Select(e => e.Written))}); one build cannot hold the right one for each. Keep one in the panel.");
            }

            return (distinct[0], null, null);
        }

        // One candidate per distinct file, in the order first seen, each naming every source that launches it.
        private static IReadOnlyList<StartupCandidate> Group(IEnumerable<(StartupExecutable Exe, string Source)> found)
        {
            return found
                .GroupBy(f => f.Exe.RelativePath, StringComparer.Ordinal)
                .Select(g => new StartupCandidate(g.First().Exe, g.Select(f => f.Source).Distinct(StringComparer.Ordinal).ToList()))
                .ToList();
        }

        private static string Capitalized(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        /// <summary>
        /// Null when <paramref name="files"/> (paths inside the build) hold what <paramref name="required"/> asks: every
        /// path, or with <see cref="StartupFiles.AnyOf"/> one of them, compared case-sensitively as Linux does; else the
        /// sentence the Push row shows. One path reads "Your game launches ./BeaconRush.x86_64; this build has no such
        /// file."; several name every file and, for the deployments, the ones missing.
        /// </summary>
        public static string MissingProblem(StartupFiles required, IEnumerable<string> files)
        {
            if (required == null || required.Paths.Count == 0)
            {
                return MissingProblem((string)null, files);
            }

            if (required.Paths.Count == 1)
            {
                return MissingProblem(required.Paths[0], files);
            }

            HashSet<string> present = Normalized(files);
            string all = string.Join(", ", required.Paths.Select(p => "./" + p));
            if (required.AnyOf)
            {
                return required.Paths.Any(present.Contains) ? null : $"Your game's template sets launch {all}; this build has none of them.";
            }

            List<string> missing = required.Paths.Where(p => !present.Contains(p)).ToList();
            return missing.Count == 0 ? null : $"Your fleet's deployments launch {all}; this build has no {string.Join(", ", missing.Select(p => "./" + p))}.";
        }

        /// <summary>The listing's paths with forward slashes, for an ordinal (case-sensitive) lookup.</summary>
        internal static HashSet<string> Normalized(IEnumerable<string> files)
        {
            return new HashSet<string>((files ?? Enumerable.Empty<string>()).Select(f => (f ?? string.Empty).Replace('\\', '/')), StringComparer.Ordinal);
        }

        /// <summary>
        /// Null when <paramref name="files"/> (paths inside the build, forward slashes) hold
        /// <paramref name="relativePath"/>, compared case-sensitively as Linux does; else the sentence the Push
        /// row shows, for example "Your game launches ./BeaconRush.x86_64; this build has no such file."
        /// </summary>
        public static string MissingProblem(string relativePath, IEnumerable<string> files)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return "The game's startup command is not known, so the build cannot be checked. " + FixInPanel;
            }

            bool present = (files ?? Enumerable.Empty<string>()).Any(f => string.Equals((f ?? string.Empty).Replace('\\', '/'), relativePath, StringComparison.Ordinal));
            return present ? null : $"Your game launches ./{relativePath}; this build has no such file.";
        }
    }
}
