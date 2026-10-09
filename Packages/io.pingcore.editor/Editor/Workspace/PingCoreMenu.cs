namespace PingCore.Editor.Workspace
{
    /// <summary>
    /// Where the plugin's one window lives, for its menu item and for the hints in messages. The
    /// window is <c>Window &gt; PingCore</c>, one scrolling page of four sections (Connect, Ship,
    /// Status, and the Player hosting fold); a message that sends the developer somewhere names the section.
    /// </summary>
    public static class PingCoreMenu
    {
        /// <summary>The menu item that opens the window.</summary>
        public const string WindowItem = "Window/PingCore";

        /// <summary>The window as messages name it.</summary>
        public const string WindowText = "Window > PingCore";

        /// <summary>Connect (sign in, pick a fleet) as messages name it.</summary>
        public const string SignInText = WindowText + ", Connect";

        /// <summary>Ship (build, push, release) as messages name it.</summary>
        public const string ShipText = WindowText + ", Ship";

        /// <summary>Status as messages name it.</summary>
        public const string StatusText = WindowText + ", Status";

        /// <summary>The Player hosting fold as messages name it.</summary>
        public const string PlayerHostingText = WindowText + ", Player hosting";
    }
}
