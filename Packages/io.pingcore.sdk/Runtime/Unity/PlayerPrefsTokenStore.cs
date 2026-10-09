using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Core.Discovery;
using UnityEngine;

namespace PingCore.Unity
{
    /// <summary>
    /// <see cref="IPlayerTokenStore"/> on <c>PlayerPrefs</c>, the Discovery client's default: the
    /// anonymous player token survives a restart, so the game does not issue a new one (and get a
    /// new player id) every launch. Keys are <c>pingcore.playerToken.&lt;profile&gt;.&lt;dscp&gt;</c>
    /// (<see cref="PlayerTokenStoreKeys.For"/>); the value is a small JSON object. Main thread only,
    /// like <c>PlayerPrefs</c>. <c>PlayerPrefs</c> is not encrypted: it holds only the anonymous
    /// token, which is per app, per player and short-lived (6 h), never a studio or backend secret.
    /// </summary>
    public sealed class PlayerPrefsTokenStore : IPlayerTokenStore
    {
        /// <inheritdoc />
        public bool TryLoad(string key, out StoredPlayerToken token)
        {
            token = null;
            if (string.IsNullOrEmpty(key) || !PlayerPrefs.HasKey(key))
            {
                return false;
            }

            return TryParse(PlayerPrefs.GetString(key, string.Empty), out token);
        }

        /// <inheritdoc />
        public void Save(string key, StoredPlayerToken token)
        {
            if (string.IsNullOrEmpty(key) || token == null)
            {
                return;
            }

            PlayerPrefs.SetString(key, Serialize(token));
            PlayerPrefs.Save();
        }

        /// <inheritdoc />
        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key) || !PlayerPrefs.HasKey(key))
            {
                return;
            }

            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }

        /// <summary>The stored form: <c>{"v":1,"token":...,"playerId":...,"expiresAtMs":...}</c>.</summary>
        public static string Serialize(StoredPlayerToken token)
        {
            var value = new JObject
            {
                ["v"] = 1,
                ["token"] = token.Token,
                ["playerId"] = token.PlayerId,
                ["expiresAtMs"] = token.ExpiresAtMs,
            };
            return value.ToString(Formatting.None);
        }

        /// <summary>Parses the stored form; false (never a throw) for anything else.</summary>
        public static bool TryParse(string stored, out StoredPlayerToken token)
        {
            token = null;
            if (string.IsNullOrEmpty(stored))
            {
                return false;
            }

            try
            {
                var value = JsonConvert.DeserializeObject<JObject>(stored, PingCoreJson.Settings);
                if (value == null || (int?)value["v"] != 1)
                {
                    return false;
                }

                string jwt = value.Value<string>("token");
                string playerId = value.Value<string>("playerId");
                long? expiresAtMs = value.Value<long?>("expiresAtMs");
                if (string.IsNullOrEmpty(jwt) || string.IsNullOrEmpty(playerId) || !expiresAtMs.HasValue)
                {
                    return false;
                }

                token = new StoredPlayerToken(jwt, playerId, expiresAtMs.Value);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (System.FormatException)
            {
                return false;
            }
            catch (System.InvalidCastException)
            {
                return false;
            }
        }
    }
}
