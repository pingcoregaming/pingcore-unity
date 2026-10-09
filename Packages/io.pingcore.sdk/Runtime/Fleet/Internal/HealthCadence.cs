namespace PingCore.Fleet
{
    /// <summary>
    /// The health ping policy, pure. Pings run only in Ready, InSession and NotReady after a
    /// 2xx Ready (a ping integrates the game, so none is sent before Ready), never in
    /// ShuttingDown or Stopping. Logged like the fleet probe's <c>logHealthPing</c>: the first,
    /// every 30th and every failure.
    /// </summary>
    internal static class HealthCadence
    {
        public const int LogEvery = 30;

        public static bool ShouldPing(FleetState state)
        {
            return state != FleetState.Inert && state != FleetState.ShuttingDown && state != FleetState.Stopping;
        }

        public static bool ShouldLog(int seq, bool ok) => !ok || seq == 1 || seq % LogEvery == 0;
    }
}
