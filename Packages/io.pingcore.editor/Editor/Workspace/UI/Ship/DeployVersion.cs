using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// Ship's build version: the default <c>yyyy.MM.dd-&lt;git short sha&gt;</c>, or
    /// <c>yyyy.MM.dd-local</c> outside a git checkout, and the version rule of
    /// <c>Cli.BuildServer</c> (1 to 64 letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, starting
    /// with a letter or digit). Pure apart from reading <c>.git</c> files.
    /// </summary>
    public static class DeployVersion
    {
        private static readonly Regex VersionPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex ShaPattern = new Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

        /// <summary>The sentence a bad version gets.</summary>
        public const string Rule = "A build version is 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit.";

        /// <summary>True when <paramref name="version"/> follows the build version rule.</summary>
        public static bool IsValid(string version) => version != null && VersionPattern.IsMatch(version);

        /// <summary><c>yyyy.MM.dd-&lt;sha&gt;</c>, or <c>-local</c> when <paramref name="gitShortSha"/> is null or empty.</summary>
        public static string Default(DateTime localNow, string gitShortSha)
        {
            string suffix = string.IsNullOrEmpty(gitShortSha) ? "local" : gitShortSha;
            return localNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + "-" + suffix;
        }

        /// <summary>
        /// The first seven characters of the commit checked out in the git repository holding
        /// <paramref name="startDirectory"/> (searched upwards), or null when there is none or it
        /// cannot be read. Reads <c>HEAD</c>, a loose ref and <c>packed-refs</c>; a <c>.git</c> file
        /// (a worktree) is followed. Never runs git.
        /// </summary>
        public static string GitShortSha(string startDirectory)
        {
            try
            {
                string gitDir = FindGitDir(startDirectory);
                if (gitDir == null)
                {
                    return null;
                }

                string head = ReadFirstLine(Path.Combine(gitDir, "HEAD"));
                if (head == null)
                {
                    return null;
                }

                string sha = head;
                if (head.StartsWith("ref: ", StringComparison.Ordinal))
                {
                    string refName = head.Substring(5).Trim();
                    sha = ReadRef(gitDir, refName);
                    if (sha == null)
                    {
                        string common = ReadFirstLine(Path.Combine(gitDir, "commondir"));
                        if (common != null)
                        {
                            sha = ReadRef(Path.GetFullPath(Path.Combine(gitDir, common)), refName);
                        }
                    }
                }

                return sha != null && ShaPattern.IsMatch(sha) ? sha.Substring(0, 7) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return null;
            }
        }

        private static string FindGitDir(string startDirectory)
        {
            if (string.IsNullOrEmpty(startDirectory))
            {
                return null;
            }

            for (DirectoryInfo dir = new DirectoryInfo(startDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                if (File.Exists(candidate))
                {
                    string line = ReadFirstLine(candidate);
                    if (line != null && line.StartsWith("gitdir:", StringComparison.Ordinal))
                    {
                        string path = line.Substring(7).Trim();
                        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir.FullName, path));
                    }

                    return null;
                }
            }

            return null;
        }

        private static string ReadRef(string gitDir, string refName)
        {
            string loose = ReadFirstLine(Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar)));
            if (loose != null)
            {
                return loose;
            }

            string packed = Path.Combine(gitDir, "packed-refs");
            if (!File.Exists(packed))
            {
                return null;
            }

            foreach (string line in File.ReadAllLines(packed))
            {
                if (line.Length > 41 && line[40] == ' ' && line.Substring(41).Trim() == refName)
                {
                    return line.Substring(0, 40);
                }
            }

            return null;
        }

        private static string ReadFirstLine(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using (var reader = new StreamReader(path))
            {
                return reader.ReadLine()?.Trim();
            }
        }
    }
}
