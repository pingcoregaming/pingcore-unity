namespace BeaconRush.Networking
{
    /// <summary>
    /// Beacon Rush's own network compatibility, independent of any platform build version: a join
    /// ticket with another <see cref="Version"/> is rejected with <c>protocol_mismatch</c>, and
    /// matchmaking queues are named per protocol (<see cref="Queue"/>).
    /// </summary>
    public static class BeaconRushProtocol
    {
        /// <summary>Raise on any change to the networked state or messages. 2: players move, beacons, the score board.</summary>
        public const int Version = 2;

        /// <summary>
        /// A client's default matchmaking queue for this protocol version, so a protocol 1 client never lands in a protocol 2
        /// match. The game server never uses it: it reads the queue from the allocation context only.
        /// </summary>
        public const string Queue = "rush-p2";

        /// <summary>The most players one game server holds.</summary>
        public const int MaxPlayers = 8;

        /// <summary>The NGO tick rate, also the dedicated server's frame rate cap.</summary>
        public const int TickRate = 30;

        /// <summary>
        /// Seconds NGO keeps an unapproved connection. Raised from NGO's 10 s so a pending approval
        /// always resolves before NGO drops the connection (the approval deadline is 10 s).
        /// </summary>
        public const int ClientConnectionBufferTimeout = 15;
    }
}
