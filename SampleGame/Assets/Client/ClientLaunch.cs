namespace BeaconRush.Client
{
    /// <summary>How this player process was launched. Set before the client scene loads.</summary>
    public static class ClientLaunch
    {
        /// <summary>
        /// True when code outside the game drives this player process without a UI (an automation harness sets it from a
        /// <c>[RuntimeInitializeOnLoadMethod]</c> before the client scene loads): no menu, no drawing, the harness decides everything.
        /// </summary>
        public static bool Headless { get; set; }

        /// <summary>True when the menu and the match view should run: an interactive player, neither headless nor batchmode.</summary>
        public static bool Interactive => !Headless && !UnityEngine.Application.isBatchMode;
    }
}
