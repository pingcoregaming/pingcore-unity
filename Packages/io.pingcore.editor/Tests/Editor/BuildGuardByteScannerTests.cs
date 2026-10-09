using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>
    /// The chunked byte scan: a token is found wherever a chunk boundary falls, in UTF-8 and UTF-16LE,
    /// and a file larger than one chunk is streamed. Token-shaped values are assembled from fragments.
    /// </summary>
    public sealed class BuildGuardByteScannerTests
    {
        private const string Tail16 = "0123456789abcdef";

        private static string Token(string prefix, string tail) => prefix + "_" + tail;

        private static byte[] Spaces(int count) => Enumerable.Repeat((byte)' ', count).ToArray();

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static BuildGuardScanResult Stream(byte[] data, int chunkSize, params string[] allowed) =>
            BuildGuardByteScanner.ScanStream(new MemoryStream(data), "f", allowed, chunkSize);

        [Test]
        public void TheOverlapIsTwiceTheLongestPrefixTheMinimumTailAndThePrecedingCharacter()
        {
            // cdnpush_ is 8 characters: 2 * (8 + 16 + 1).
            Assert.That(BuildGuardByteScanner.Overlap, Is.EqualTo(50));
        }

        [Test]
        public void AUtf8TokenStraddlingAChunkBoundaryIsFoundForEveryChunkSize()
        {
            byte[] data = Concat(Spaces(55), Encoding.ASCII.GetBytes(Token("usr", "abcd" + Tail16)), Spaces(40));
            for (int chunk = 1; chunk <= 130; chunk++)
            {
                BuildGuardScanResult result = Stream(data, chunk);
                Assert.That(result.Findings.Count, Is.EqualTo(1), "chunk size " + chunk);
                Assert.That(result.Findings[0].Detail, Is.EqualTo("usr_ token-shaped string (24 chars) at byte 55 (UTF-8)"), "chunk size " + chunk);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void AUtf16LeTokenStraddlingAChunkBoundaryIsFoundForEveryChunkSize(int leadingOddBytes)
        {
            byte[] data = Concat(Spaces(leadingOddBytes), Encoding.Unicode.GetBytes(new string(' ', 30) + Token("cdnpush", Tail16) + "  "));
            int expectedOffset = leadingOddBytes + 60;
            for (int chunk = 1; chunk <= 130; chunk++)
            {
                BuildGuardScanResult result = Stream(data, chunk);
                Assert.That(result.Findings.Count, Is.EqualTo(1), "chunk size " + chunk);
                Assert.That(result.Findings[0].Detail,
                    Is.EqualTo("cdnpush_ token-shaped string (24 chars) at byte " + expectedOffset + " (UTF-16LE)"), "chunk size " + chunk);
            }
        }

        [Test]
        public void ALetterBeforeTheTokenInThePreviousChunkStillSuppressesTheMatch()
        {
            byte[] suppressed = Concat(Spaces(63), Encoding.ASCII.GetBytes("a" + Token("usr", Tail16)), Spaces(10));
            byte[] matched = Concat(Spaces(63), Encoding.ASCII.GetBytes("-" + Token("usr", Tail16)), Spaces(10));
            foreach (int chunk in new[] { 1, 7, 63, 64, 65, 100 })
            {
                Assert.That(Stream(suppressed, chunk).Findings, Is.Empty, "chunk size " + chunk);
                Assert.That(Stream(matched, chunk).Findings.Count, Is.EqualTo(1), "chunk size " + chunk);
            }
        }

        /// <summary>
        /// The UTF-16LE preceding character is two bytes, so the scanner must keep two bytes before the
        /// earliest resume position. Keeping only one (dropping the low byte of the "a") turns the
        /// suppressed case into a match for chunk sizes 1, 2, 3, 6 and others; this sweep catches that.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        public void AUtf16LeLetterBeforeTheTokenInThePreviousChunkStillSuppressesTheMatch(int leadingOddBytes)
        {
            byte[] suppressed = Concat(Spaces(leadingOddBytes), Encoding.Unicode.GetBytes(new string(' ', 31) + "a" + Token("usr", Tail16) + "     "));
            byte[] matched = Concat(Spaces(leadingOddBytes), Encoding.Unicode.GetBytes(new string(' ', 31) + "-" + Token("usr", Tail16) + "     "));
            for (int chunk = 1; chunk <= 130; chunk++)
            {
                Assert.That(Stream(suppressed, chunk).Findings, Is.Empty, "chunk size " + chunk);
                Assert.That(Stream(matched, chunk).Findings.Count, Is.EqualTo(1), "chunk size " + chunk);
            }
        }

        [Test]
        public void ATokenLongerThanTheOverlapIsHeldUntilItEndsSoItsFullValueIsCompared()
        {
            string longToken = Token("dsc", new string('A', 200));
            byte[] data = Concat(Spaces(5), Encoding.ASCII.GetBytes(longToken), Spaces(5));
            foreach (int chunk in new[] { 1, 16, 49, 50, 51, 128 })
            {
                BuildGuardScanResult allowed = Stream(data, chunk, longToken);
                Assert.That(allowed.Findings, Is.Empty, "chunk size " + chunk);
                Assert.That(allowed.AllowedDscTokenHits, Is.EqualTo(1), "chunk size " + chunk);

                BuildGuardScanResult refused = Stream(data, chunk, Token("dsc", new string('A', 199)));
                Assert.That(refused.Findings.Count, Is.EqualTo(1), "chunk size " + chunk);
                Assert.That(refused.Findings[0].Detail, Is.EqualTo("dsc_ token-shaped string (204 chars) at byte 5 (UTF-8)"), "chunk size " + chunk);
            }
        }

        [Test]
        public void A15CharacterTailAtTheEndOfTheStreamIsNotASecretLiteral()
        {
            byte[] data = Concat(Spaces(70), Encoding.ASCII.GetBytes(Token("usr", "0123456789abcde")));
            foreach (int chunk in new[] { 1, 32, 64, 1024 })
            {
                Assert.That(Stream(data, chunk).Findings, Is.Empty, "chunk size " + chunk);
            }
        }

        [Test]
        public void AFileLargerThanOneDefaultChunkIsStreamedAndATokenOnTheBoundaryIsFound()
        {
            string path = Path.Combine(Path.GetTempPath(), "pingcore-guard-large-" + Guid.NewGuid().ToString("N") + ".bin");
            int offset = BuildGuardByteScanner.DefaultChunkSize - 10;
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    byte[] filler = Spaces(64 * 1024);
                    int written = 0;
                    while (written < offset)
                    {
                        int count = Math.Min(filler.Length, offset - written);
                        file.Write(filler, 0, count);
                        written += count;
                    }

                    byte[] token = Encoding.ASCII.GetBytes(Token("sys", Tail16 + Tail16));
                    file.Write(token, 0, token.Length);
                    file.Write(filler, 0, 100);
                }

                BuildGuardScanResult result = BuildGuardFileScanner.ScanFile(path, "big.bin", null);
                Assert.That(result.Findings.Count, Is.EqualTo(1));
                Assert.That(result.Findings[0].Detail, Is.EqualTo("sys_ token-shaped string (36 chars) at byte " + offset + " (UTF-8)"));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void AMissingFileFailsClosed()
        {
            string path = Path.Combine(Path.GetTempPath(), "pingcore-guard-missing-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<BuildGuardReadException>(() => BuildGuardFileScanner.ScanFile(path, "missing", null));
        }
    }
}
