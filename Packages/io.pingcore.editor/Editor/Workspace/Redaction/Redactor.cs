using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PingCore.Editor.Workspace.Redaction
{
    /// <summary>
    /// Masks credentials in any text the plugin shows or logs: child process output, API
    /// messages and exception text. Pure. It masks, in this order, every known secret value it
    /// was given (exact match), then every token-shaped run
    /// (<c>(usr|sys|cdnpush|dsc)_</c> followed by 16 or more letters or digits, the build
    /// guard's pattern), then every run of 64 or more hex digits (CDN tokens, digests, the push
    /// status <c>token</c> field), then every game server key (<c>&lt;id&gt;-&lt;32 hex&gt;</c>,
    /// whose id is kept). <c>dscp_</c> public ids are not secrets and are left alone.
    /// </summary>
    public sealed class Redactor
    {
        /// <summary>What replaces a known secret value.</summary>
        public const string Mask = "[redacted]";

        /// <summary>Known values shorter than this are not masked by value (they would mask ordinary words).</summary>
        public const int MinimumKnownSecretLength = 8;

        private static readonly Regex TokenPattern = new Regex(
            "(?<![A-Za-z0-9])(usr|sys|cdnpush|dsc)_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant);

        // The refusal (arguments and settings files) is stricter than the mask, as the push script's is:
        // a prefix and eight characters is already a token someone pasted into the wrong field.
        private static readonly Regex ArgumentTokenPattern = new Regex(
            "(?<![A-Za-z0-9])(usr|sys|cdnpush|dsc)_[A-Za-z0-9]{8,}", RegexOptions.CultureInvariant);

        private static readonly Regex HexRunPattern = new Regex(
            "(?<![0-9A-Fa-f])[0-9A-Fa-f]{64,}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);

        private static readonly Regex GameServerKeyPattern = new Regex(
            "(?<![A-Za-z0-9])([0-9]+)-[0-9A-Fa-f]{32}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);

        private readonly string[] knownSecrets;

        /// <summary>A redactor that masks the patterns only.</summary>
        public Redactor()
            : this(null)
        {
        }

        /// <param name="knownSecrets">Exact values to mask wherever they appear (a pasted key, a
        /// push token handed to a child). Null and short entries are ignored.</param>
        public Redactor(IEnumerable<string> knownSecrets)
        {
            this.knownSecrets = (knownSecrets ?? Enumerable.Empty<string>())
                .Where(s => s != null && s.Length >= MinimumKnownSecretLength)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length)
                .ToArray();
        }

        /// <summary>The shared pattern-only redactor.</summary>
        public static Redactor PatternsOnly { get; } = new Redactor();

        /// <summary>A redactor that also masks <paramref name="extra"/>.</summary>
        public Redactor With(IEnumerable<string> extra)
        {
            return new Redactor(knownSecrets.Concat(extra ?? Enumerable.Empty<string>()));
        }

        /// <summary>Returns <paramref name="text"/> with every secret masked; null stays null.</summary>
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string result = text;
            foreach (string secret in knownSecrets)
            {
                if (result.IndexOf(secret, StringComparison.Ordinal) >= 0)
                {
                    result = result.Replace(secret, Mask);
                }
            }

            result = TokenPattern.Replace(result, m => m.Groups[1].Value + "_" + Mask);
            result = HexRunPattern.Replace(result, Mask);
            result = GameServerKeyPattern.Replace(result, m => m.Groups[1].Value + "-" + Mask);
            return result;
        }

        /// <summary>True when <paramref name="text"/> holds anything <see cref="Redact"/> would mask by pattern.</summary>
        public static bool ContainsSecret(string text)
        {
            return !string.IsNullOrEmpty(text)
                && (TokenPattern.IsMatch(text) || HexRunPattern.IsMatch(text) || GameServerKeyPattern.IsMatch(text));
        }

        /// <summary>
        /// True when a value someone could read looks like a credential: a token prefix and eight or
        /// more letters or digits, a 64-hex run or a game server key. The one refusal rule for
        /// command-line arguments (readable by every user of the machine) and settings files
        /// (readable by whoever opens the project).
        /// </summary>
        public static bool LooksLikeCredential(string value)
        {
            return !string.IsNullOrEmpty(value)
                && (ArgumentTokenPattern.IsMatch(value) || HexRunPattern.IsMatch(value) || GameServerKeyPattern.IsMatch(value));
        }

        /// <summary>True when a command-line argument looks like a credential (<see cref="LooksLikeCredential"/>); a child process is refused such an argument.</summary>
        public static bool LooksLikeCredentialArgument(string argument) => LooksLikeCredential(argument);
    }
}
