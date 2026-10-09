using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PingCore.Editor.Cli
{
    /// <summary>
    /// The exact bytes of one project settings file, taken before a build changes a player setting and written back
    /// afterwards. Restoring the setting through <c>PlayerSettings</c> is not enough: when Unity saves
    /// <c>ProjectSettings/ProjectSettings.asset</c> it re-serialises the whole file with this editor's defaults for keys
    /// the file left empty (in 6000.4: <c>targetPixelDensity: 30</c>, the iOS, tvOS, visionOS and macOS minimum
    /// versions, a per-platform <c>buildNumber</c>, an explicit <c>scriptingBackend</c> entry), so a build that set the
    /// backend and set it back still left a diff. <see cref="BuildHousekeeping.Finish"/> restores after Unity has saved
    /// its own version, so the editor has nothing dirty left to write over it. The file IO is plain, so the tests run it
    /// on a temporary folder.
    /// </summary>
    public sealed class SettingsFileSnapshot
    {
        private readonly byte[] original;

        private SettingsFileSnapshot(string fullPath, byte[] original)
        {
            FullPath = fullPath;
            this.original = original;
        }

        /// <summary>The file's full path.</summary>
        public string FullPath { get; }

        /// <summary>True when the file existed at capture.</summary>
        public bool Existed => original != null;

        /// <summary>The SHA-256 of the captured bytes, lower-case hex, or null when the file did not exist.</summary>
        public string OriginalHash => original == null ? null : Sha256Hex(original);

        /// <summary>Reads the file's bytes now; a missing file is captured as missing.</summary>
        public static SettingsFileSnapshot Capture(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                throw new ArgumentException("a path is required", nameof(fullPath));
            }

            return new SettingsFileSnapshot(fullPath, File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null);
        }

        /// <summary>
        /// Writes the captured bytes back when the file now differs, and returns true when it wrote. A file that did not
        /// exist at capture is left as Unity made it: deleting a settings file Unity just created would be worse than a diff.
        /// </summary>
        public bool Restore()
        {
            byte[] current = File.Exists(FullPath) ? File.ReadAllBytes(FullPath) : null;
            if (!NeedsRestore(original, current))
            {
                return false;
            }

            File.WriteAllBytes(FullPath, original);
            return true;
        }

        /// <summary>True when the file reads back exactly as captured (or was missing and still is).</summary>
        public bool IsIntact()
        {
            byte[] current = File.Exists(FullPath) ? File.ReadAllBytes(FullPath) : null;
            return original == null ? current == null : current != null && SameBytes(original, current);
        }

        /// <summary>Whether <paramref name="current"/> must be overwritten with <paramref name="original"/>. Pure.</summary>
        public static bool NeedsRestore(byte[] original, byte[] current) =>
            original != null && (current == null || !SameBytes(original, current));

        /// <summary>The SHA-256 of <paramref name="data"/>, lower-case hex. Pure.</summary>
        public static string Sha256Hex(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    text.Append(b.ToString("x2"));
                }

                return text.ToString();
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
