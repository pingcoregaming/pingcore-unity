using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// Opens and scans files. A file is streamed through <see cref="BuildGuardByteScanner"/> in
    /// chunks, so its size does not matter. On Windows a path past the legacy length limit is retried
    /// with the extended-length prefix. A file that still cannot be read fails the build: the guard
    /// never skips what it cannot see.
    /// </summary>
    public static class BuildGuardFileScanner
    {
        private const string ExtendedLengthPrefix = @"\\?\";

        /// <summary>Byte-scans one file as UTF-8 and UTF-16LE.</summary>
        public static BuildGuardScanResult ScanFile(string path, string displayPath, IReadOnlyCollection<string> allowedDscTokens,
            int chunkSize = BuildGuardByteScanner.DefaultChunkSize)
        {
            using (Stream stream = OpenForScan(path))
            {
                try
                {
                    return BuildGuardByteScanner.ScanStream(stream, displayPath, allowedDscTokens, chunkSize);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
                {
                    throw ReadFailure(path, e);
                }
            }
        }

        /// <summary>A source file's text with its byte order mark honoured (UTF-8 without one).</summary>
        public static string ReadText(string path)
        {
            using (Stream stream = OpenForScan(path))
            {
                try
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
                {
                    throw ReadFailure(path, e);
                }
            }
        }

        private static Stream OpenForScan(string path)
        {
            try
            {
                return Open(path);
            }
            catch (Exception first) when (IsReadError(first))
            {
                if (Path.DirectorySeparatorChar == '\\' && !path.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal))
                {
                    try
                    {
                        return Open(ExtendedLengthPrefix + Path.GetFullPath(path).Replace('/', '\\'));
                    }
                    catch (Exception retry) when (IsReadError(retry) || retry is ArgumentException)
                    {
                        // Fall through to the failure below with the first error.
                    }
                }

                throw ReadFailure(path, first);
            }
        }

        private static Stream Open(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

        private static bool IsReadError(Exception e) =>
            e is IOException || e is UnauthorizedAccessException || e is NotSupportedException;

        private static BuildGuardReadException ReadFailure(string path, Exception error) =>
            new BuildGuardReadException("PingCore build guard could not read " + path + " to scan it (" + error.GetType().Name
                + "). The guard fails closed: shorten the project path or fix the file, then build again.");
    }
}
