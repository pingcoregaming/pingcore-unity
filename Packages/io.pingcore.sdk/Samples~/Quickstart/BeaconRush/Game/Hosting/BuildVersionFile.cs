using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// Reads <c>version.txt</c>, which <c>PingCore.Editor.Cli.BuildServer</c> writes beside the
    /// executable: the platform build version, not the game's protocol version.
    /// </summary>
    public static class BuildVersionFile
    {
        public const string FileName = "version.txt";

        private static readonly Regex VersionPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        /// <summary>The version beside the running player, or null in the Editor or when the file is absent or malformed.</summary>
        public static string Read()
        {
            if (Application.isEditor)
            {
                return null;
            }

            try
            {
                // Application.dataPath is <dir>/<product>_Data in a Linux or Windows player.
                string folder = Path.GetDirectoryName(Application.dataPath);
                return folder == null ? null : Parse(File.ReadAllText(Path.Combine(folder, FileName)));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The first line, trimmed, when it is a valid build version; else null.</summary>
        public static string Parse(string text)
        {
            if (text == null)
            {
                return null;
            }

            int end = text.IndexOfAny(new[] { '\r', '\n' });
            string line = (end >= 0 ? text.Substring(0, end) : text).Trim();
            return VersionPattern.IsMatch(line) ? line : null;
        }
    }
}
