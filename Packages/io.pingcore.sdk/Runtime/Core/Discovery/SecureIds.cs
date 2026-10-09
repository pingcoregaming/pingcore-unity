using System;
using System.Security.Cryptography;
using System.Text;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// Identifiers the SDK mints itself (ticket ids, reservation ids, idempotency keys), the
    /// short reference that stands in for one in logs and events, and the SDK's only random source.
    /// </summary>
    public static class SecureIds
    {
        /// <summary>
        /// 128 random bits from <see cref="RandomNumberGenerator"/>, base64url without padding:
        /// 22 characters from <c>[A-Za-z0-9_-]</c>, so it matches Discovery's id pattern
        /// <c>^[A-Za-z0-9_.:-]{1,100}$</c>. A ticket id is a bearer secret for its ticket.
        /// </summary>
        public static string NewId128()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// A uniform value in [0, 1) from <see cref="RandomNumberGenerator"/> (53 random bits), for jitter.
        /// The SDK has this one random source and never the framework's seeded pseudo-random
        /// generator (a source scan, <c>RuntimeRandomBanTests</c>, pins it).
        /// </summary>
        public static double NextUnit()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            ulong bits = BitConverter.ToUInt64(bytes, 0) >> 11;
            return bits / (double)(1UL << 53);
        }

        /// <summary>
        /// The first 12 lower-case hex characters of the SHA-256 of <paramref name="id"/> (UTF-8):
        /// the only form of a ticket id that logs and events carry. Null and empty map to an empty string.
        /// </summary>
        public static string Ref(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return string.Empty;
            }

            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(id));
            }

            var hex = new StringBuilder(12);
            for (int i = 0; i < 6; i++)
            {
                hex.Append(digest[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return hex.ToString();
        }

        /// <summary>True when <paramref name="id"/> matches Discovery's reservation and player ticket id pattern <c>^[A-Za-z0-9_.:-]{1,100}$</c>.</summary>
        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 100)
            {
                return false;
            }

            foreach (char c in id)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '_' || c == '.' || c == ':' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
