using System;
using System.Collections.Generic;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// Where a client keeps its anonymous player token between runs, so a restart reuses it
    /// instead of issuing again (issuance is 10 per minute per source IP, shared across apps).
    /// Keys come from <see cref="PlayerTokenStoreKeys.For"/>. An implementation never logs a token.
    /// Unity's default is <c>PingCore.Unity.PlayerPrefsTokenStore</c>; <see cref="MemoryTokenStore"/>
    /// keeps nothing across runs.
    /// </summary>
    public interface IPlayerTokenStore
    {
        /// <summary>Loads the token stored under <paramref name="key"/>; false when there is none or it does not parse.</summary>
        bool TryLoad(string key, out StoredPlayerToken token);

        /// <summary>Stores <paramref name="token"/> under <paramref name="key"/>, replacing any previous one.</summary>
        void Save(string key, StoredPlayerToken token);

        /// <summary>Removes the token stored under <paramref name="key"/>, if any.</summary>
        void Delete(string key);
    }

    /// <summary>A persisted anonymous player token. <see cref="ToString"/> never shows the token.</summary>
    public sealed class StoredPlayerToken
    {
        /// <summary>Creates a stored token.</summary>
        /// <param name="token">The compact JWT. A bearer secret.</param>
        /// <param name="playerId">The token's <c>sub</c>, for example <c>anon:&lt;uuid&gt;</c>.</param>
        /// <param name="expiresAtMs">Expiry, epoch milliseconds.</param>
        public StoredPlayerToken(string token, string playerId, long expiresAtMs)
        {
            Token = token;
            PlayerId = playerId;
            ExpiresAtMs = expiresAtMs;
        }

        /// <summary>The compact JWT. Never log it.</summary>
        public string Token { get; }

        /// <summary>The player id the token names.</summary>
        public string PlayerId { get; }

        /// <summary>Expiry, epoch milliseconds.</summary>
        public long ExpiresAtMs { get; }

        /// <summary>Expiry as a time.</summary>
        public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMs);

        /// <inheritdoc />
        public override string ToString() => $"StoredPlayerToken(playerId={PlayerId}, expiresAtMs={ExpiresAtMs})";
    }

    /// <summary>The store keys: one token per profile per Discovery app.</summary>
    public static class PlayerTokenStoreKeys
    {
        /// <summary>
        /// <c>pingcore.playerToken.&lt;profile&gt;.&lt;dscp&gt;</c>. The profile separates several
        /// clients on one PC that share <c>PlayerPrefs</c> (each would otherwise get the same
        /// player id, and a player holds one queued ticket); the default profile is empty.
        /// </summary>
        public static string For(string profile, string appPublicId)
        {
            return "pingcore.playerToken." + (profile ?? string.Empty) + "." + (appPublicId ?? string.Empty);
        }

        /// <summary>True for an empty profile or one of 1 to 32 characters from <c>[A-Za-z0-9_-]</c>.</summary>
        public static bool IsValidProfile(string profile)
        {
            if (string.IsNullOrEmpty(profile))
            {
                return true;
            }

            if (profile.Length > 32)
            {
                return false;
            }

            foreach (char c in profile)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A store that lives as long as the object: nothing survives a restart.</summary>
    public sealed class MemoryTokenStore : IPlayerTokenStore
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, StoredPlayerToken> tokens = new Dictionary<string, StoredPlayerToken>(StringComparer.Ordinal);

        /// <summary>How many tokens are stored.</summary>
        public int Count
        {
            get
            {
                lock (gate)
                {
                    return tokens.Count;
                }
            }
        }

        /// <inheritdoc />
        public bool TryLoad(string key, out StoredPlayerToken token)
        {
            lock (gate)
            {
                return tokens.TryGetValue(key ?? string.Empty, out token);
            }
        }

        /// <inheritdoc />
        public void Save(string key, StoredPlayerToken token)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            lock (gate)
            {
                tokens[key ?? string.Empty] = token;
            }
        }

        /// <inheritdoc />
        public void Delete(string key)
        {
            lock (gate)
            {
                tokens.Remove(key ?? string.Empty);
            }
        }
    }
}
