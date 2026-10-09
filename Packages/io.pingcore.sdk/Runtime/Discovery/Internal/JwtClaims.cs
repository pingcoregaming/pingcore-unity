using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// Reads <c>sub</c> and <c>exp</c> from a compact JWT's payload WITHOUT verifying it: the client
    /// only needs to know whose token it holds and when to refresh it; Discovery verifies.
    /// </summary>
    internal static class JwtClaims
    {
        /// <summary>False (never a throw) when the token is not three base64url parts with a JSON payload holding a string <c>sub</c> and a numeric <c>exp</c>.</summary>
        public static bool TryRead(string jwt, out string subject, out DateTimeOffset expiresAt)
        {
            subject = null;
            expiresAt = default;
            if (string.IsNullOrEmpty(jwt) || jwt.Length > 8192)
            {
                return false;
            }

            string[] parts = jwt.Split('.');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return false;
            }

            byte[] payloadBytes = DecodeBase64Url(parts[1]);
            if (payloadBytes == null)
            {
                return false;
            }

            try
            {
                var payload = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(payloadBytes), PingCoreJson.Settings);
                if (payload == null || !(payload["sub"] is JValue sub) || sub.Type != JTokenType.String)
                {
                    return false;
                }

                if (!(payload["exp"] is JValue exp) || (exp.Type != JTokenType.Integer && exp.Type != JTokenType.Float))
                {
                    return false;
                }

                double seconds = Convert.ToDouble(exp.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (double.IsNaN(seconds) || seconds < 0 || seconds > 253402300799)
                {
                    return false;
                }

                string value = (string)sub.Value;
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }

                subject = value;
                expiresAt = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(seconds));
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Decodes base64url (no padding); null when it is not.</summary>
        internal static byte[] DecodeBase64Url(string text)
        {
            string s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 0:
                    break;
                case 2:
                    s += "==";
                    break;
                case 3:
                    s += "=";
                    break;
                default:
                    return null;
            }

            try
            {
                return Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
