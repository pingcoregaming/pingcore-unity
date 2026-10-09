namespace PingCore.Discovery.Client
{
    /// <summary>Location id and latency value rules shared by the list, quick join and tickets.</summary>
    internal static class LatencyKeys
    {
        /// <summary>Discovery's latency ceiling (<c>config.matchmaker.maxLatencyMs</c> default).</summary>
        public const int MaxLatencyMs = 10000;

        /// <summary>True when <paramref name="id"/> matches <c>^[A-Za-z0-9_-]{1,30}$</c>.</summary>
        public static bool IsValidLocationId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 30)
            {
                return false;
            }

            foreach (char c in id)
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
}
