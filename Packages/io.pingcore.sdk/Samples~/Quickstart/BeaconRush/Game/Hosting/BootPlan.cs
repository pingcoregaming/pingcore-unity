namespace BeaconRush.Hosting
{
    /// <summary>When the first <c>players</c> write happens in the boot sequence.</summary>
    public enum FirstPlayersWrite
    {
        /// <summary>After <c>StartAsync</c> and before listening: the deliberate integrating write (the default).</summary>
        BeforeListening,

        /// <summary>After a 2xx <c>ReadyAsync</c>, so a call made before Ready is the only write the endpoint sees first.</summary>
        AfterReady,
    }

    /// <summary>
    /// The boot order decision, pure. By default the game integrates with <c>players = 0</c> before it listens. When the
    /// installed <see cref="ServerInstrumentation"/> asks for it (<see cref="ServerInstrumentation.DeferFirstPlayersWrite"/>),
    /// that write waits until after a 2xx Ready, so a call made in <see cref="ServerInstrumentation.BeforeReadyAsync"/> is
    /// the only write before <c>/ready</c>. This is the intent only; each write reports its own return
    /// (<see cref="ServerInstrumentation.PlayersWriteReturned"/>).
    /// </summary>
    public sealed class BootPlan
    {
        private BootPlan(FirstPlayersWrite firstPlayersWrite)
        {
            FirstPlayersWrite = firstPlayersWrite;
        }

        public FirstPlayersWrite FirstPlayersWrite { get; }

        /// <summary>The plan: the default order unless <paramref name="deferFirstPlayersWrite"/>.</summary>
        public static BootPlan For(bool deferFirstPlayersWrite)
        {
            return new BootPlan(deferFirstPlayersWrite ? FirstPlayersWrite.AfterReady : FirstPlayersWrite.BeforeListening);
        }
    }
}
