using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// The pingctl a push actually runs. For the bundled binary that is a private copy: the binary is copied into a new,
    /// randomly named folder under <c>&lt;project&gt;/Library/PingCore/pingctl-run/</c>, the copy is opened for reading
    /// with writes and deletes denied to everyone else, its SHA-256 is computed through that open handle and compared
    /// with the manifest's, and the handle stays open until the push is over (<see cref="Dispose"/>, which then deletes
    /// the folder). So the bytes that were checked are the bytes that run: on Windows nothing can change the copy between
    /// the check and the start, because the open handle refuses every writer; on macOS and Linux a file lock is only
    /// advisory, so there a process running as the same user could still write into that fresh folder in the moment
    /// between the check and the start. A developer's own pingctl (Ship's path or <c>PINGCTL_BIN</c>) has no manifest and
    /// runs in place, unchecked: it is the developer's choice.
    /// </summary>
    public sealed class PingctlRunCopy : IDisposable
    {
        /// <summary>The folder under the project that holds the run copies.</summary>
        public const string RelativeFolder = "Library/PingCore/pingctl-run";

        // The run folders a copy of this Editor still holds. The sweep never touches them: on macOS and Linux a held file
        // can still be deleted, which would pull a running push's pingctl (or Detect's) out from under it.
        private static readonly HashSet<string> Live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private FileStream held;
        private string folder;

        private PingctlRunCopy(string path, bool bundled, string problem, FileStream held, string folder)
        {
            Path = path;
            Bundled = bundled;
            Problem = problem;
            this.held = held;
            this.folder = folder;
        }

        /// <summary>The executable to run, or null with <see cref="Problem"/>.</summary>
        public string Path { get; }

        /// <summary>True when <see cref="Path"/> is a checked copy of the bundled binary.</summary>
        public bool Bundled { get; }

        /// <summary>Why nothing may run, or null.</summary>
        public string Problem { get; }

        public bool Ok => Path != null;

        /// <summary>A pingctl that runs where it is (the developer's own, or a test double).</summary>
        public static PingctlRunCopy InPlace(string path) => new PingctlRunCopy(path, false, null, null, null);

        /// <summary>
        /// The copy to run for <paramref name="location"/>: in place for a developer's own pingctl, else a checked,
        /// held private copy under <paramref name="projectRoot"/>. Never throws for a file system failure; the problem
        /// names the exception's type.
        /// </summary>
        public static PingctlRunCopy Prepare(string projectRoot, PingctlLocation location)
        {
            if (location == null || !location.Found)
            {
                return Refused(location?.Problem ?? "pingctl was not found.");
            }

            if (location.ExpectedSha256 == null)
            {
                return InPlace(location.Path);
            }

            SweepStale(projectRoot);
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, "Library", "PingCore", "pingctl-run", Guid.NewGuid().ToString("N")));
            lock (Live)
            {
                Live.Add(dir);
            }

            FileStream stream = null;
            try
            {
                Directory.CreateDirectory(dir);
                string copy = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(location.Path));
                File.Copy(location.Path, copy, false);
                stream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
                string actual = Sha256Of(stream);
                if (!string.Equals(actual, location.ExpectedSha256, StringComparison.Ordinal))
                {
                    stream.Dispose();
                    DeleteQuietly(dir);
                    return Refused("The copy of the bundled pingctl does not match its pinned SHA-256, so it is not run. Reinstall the PingCore Editor package, or set your own pingctl.");
                }

                return new PingctlRunCopy(copy, true, null, stream, dir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
                stream?.Dispose();
                DeleteQuietly(dir);
                return Refused($"The bundled pingctl could not be copied and checked before it runs ({e.GetType().Name}).");
            }
        }

        /// <summary>Closes the held copy and deletes its folder; a folder still in use is left for the next push to sweep.</summary>
        public void Dispose()
        {
            held?.Dispose();
            held = null;
            if (folder != null)
            {
                DeleteQuietly(folder);
                folder = null;
            }
        }

        private static PingctlRunCopy Refused(string problem) => new PingctlRunCopy(null, false, problem, null, null);

        private static string Sha256Of(Stream stream)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(stream);
                var hex = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest)
                {
                    hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }

                return hex.ToString();
            }
        }

        // Earlier pushes' copies the Editor could not delete (a pingctl still running, an Editor that stopped); never a
        // folder a copy in this Editor still holds.
        private static void SweepStale(string projectRoot)
        {
            string root = System.IO.Path.Combine(projectRoot, "Library", "PingCore", "pingctl-run");
            if (!Directory.Exists(root))
            {
                return;
            }

            try
            {
                foreach (string stale in Directory.GetDirectories(root))
                {
                    bool live;
                    lock (Live)
                    {
                        live = Live.Contains(System.IO.Path.GetFullPath(stale));
                    }

                    if (!live)
                    {
                        DeleteQuietly(stale);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Best effort: a folder in use stays until a later push.
            }
        }

        private static void DeleteQuietly(string dir)
        {
            lock (Live)
            {
                Live.Remove(dir);
            }

            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Best effort, as above.
            }
        }
    }
}
