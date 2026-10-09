using System.Globalization;
using System.Text;
using Unity.Collections;

namespace BeaconRush.Match
{
    /// <summary>
    /// A player's display name on the score board, pure. The name comes from the join ticket's optional
    /// <c>displayName</c>, which is untrusted text: control and format characters are dropped, whitespace runs
    /// become one space, and the result is cut to <see cref="MaxBytes"/> UTF-8 bytes at a character boundary so it
    /// fits a <see cref="FixedString32Bytes"/>. An empty result is <c>Player &lt;clientId&gt;</c>.
    /// </summary>
    public static class DisplayNames
    {
        /// <summary>The most UTF-8 bytes a <see cref="FixedString32Bytes"/> holds.</summary>
        public const int MaxBytes = 29;

        /// <summary>The cleaned name, or null when nothing printable is left.</summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var text = new StringBuilder();
            bool pendingSpace = false;
            int bytes = 0;
            for (int i = 0; i < name.Length; i++)
            {
                string element = char.IsSurrogatePair(name, i) ? name.Substring(i++, 2) : name[i].ToString();
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format || category == UnicodeCategory.Surrogate
                    || category == UnicodeCategory.PrivateUse || category == UnicodeCategory.OtherNotAssigned)
                {
                    continue;
                }

                if (char.IsWhiteSpace(element, 0))
                {
                    pendingSpace = text.Length > 0;
                    continue;
                }

                int needed = Encoding.UTF8.GetByteCount(element) + (pendingSpace ? 1 : 0);
                if (bytes + needed > MaxBytes)
                {
                    break;
                }

                if (pendingSpace)
                {
                    text.Append(' ');
                    pendingSpace = false;
                }

                text.Append(element);
                bytes += needed;
            }

            return text.Length == 0 ? null : text.ToString();
        }

        /// <summary>The name to show for <paramref name="clientId"/>: the cleaned name, else <c>Player &lt;clientId&gt;</c>.</summary>
        public static string ForClient(string name, ulong clientId) => Clean(name) ?? "Player " + clientId.ToString(CultureInfo.InvariantCulture);

        /// <summary>The name as a <see cref="FixedString32Bytes"/>.</summary>
        public static FixedString32Bytes ToFixed(string name, ulong clientId) => new FixedString32Bytes(ForClient(name, clientId));
    }
}
