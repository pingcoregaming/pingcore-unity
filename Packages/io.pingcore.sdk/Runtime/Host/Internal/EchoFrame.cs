namespace PingCore.Discovery.Host
{
    /// <summary>
    /// The <c>udp-echo</c> challenge Discovery sends to prove a heartbeat-tier game server owns its
    /// endpoint: the 5 ASCII bytes <c>DSCV1</c>
    /// and a 16-byte random nonce, 21 bytes. The prober requires the exact same bytes back
    /// (equal length and equal content) and ignores any other datagram.
    /// </summary>
    internal static class EchoFrame
    {
        /// <summary>The magic prefix, <c>DSCV1</c> in ASCII.</summary>
        public static readonly byte[] Magic = { 0x44, 0x53, 0x43, 0x56, 0x31 };

        /// <summary>Nonce length.</summary>
        public const int NonceLength = 16;

        /// <summary>The whole challenge: magic plus nonce.</summary>
        public const int Length = 21;

        /// <summary>True when the first <paramref name="count"/> bytes of <paramref name="buffer"/> are a challenge.</summary>
        public static bool IsChallenge(byte[] buffer, int count)
        {
            if (buffer == null || count != Length || buffer.Length < Length)
            {
                return false;
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (buffer[i] != Magic[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
