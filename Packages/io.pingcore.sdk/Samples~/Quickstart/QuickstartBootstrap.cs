using System.Collections;
using BeaconRush.Client;
using BeaconRush.Client.UI;
using BeaconRush.Networking;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Quickstart
{
    /// <summary>
    /// The Quickstart's one script. The scene is the Beacon Rush client (its menu, Find match, Browse, Host and Direct
    /// connect screens, the match view and the HUD, all copied under <c>BeaconRush/</c>), driven by
    /// <c>QuickstartClientSettings.asset</c>: the Discovery URL and the public ids of the Beacon Rush fleet app and the
    /// community app. This component puts two entries at the top of the menu:
    /// <list type="bullet">
    /// <item><b>Quick play</b> (<see cref="QuickPlay"/>, the same as <see cref="QuickJoinIdleHostedGameServer"/>):
    /// Discovery's quick join holds a seat on the best Beacon Rush game server with room, an idle one included, and the
    /// player joins it alone: the idle game server claims itself for that session (a self-allocation through the local
    /// SDK endpoint, so the matchmaker skips it) before it lets the player in, and the match starts with the first
    /// player. When no game server has a seat it falls back to Find match, which waits for a second player and says so;
    /// when the game server was taken between the hold and the join it tries once more.</item>
    /// <item><b>Play on this PC</b> (<see cref="PlayOnThisPc"/>): hosts a LAN-only game on this PC and plays in it, with no
    /// PingCore service involved. A second copy of the game joins it through Direct connect at
    /// <c>127.0.0.1:7777</c>.</item>
    /// </list>
    /// It drives the copied client only through its UI Toolkit tree (the element names in <c>ClientMenu.uxml</c>) and its
    /// public <see cref="ClientUi.Screen"/>, so the copy stays byte for byte what Beacon Rush ships.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class QuickstartBootstrap : MonoBehaviour
    {
        /// <summary>The Find match screen's note: why a lone player waits, and what to do about it.</summary>
        public const string SecondPlayerNote =
            "A match from the queue starts when two players are queued. Ask a friend to press Find match too, or start a second "
            + "player (Multiplayer Play Mode, or a second copy of the game) and press Find match there. Alone? Go back and "
            + "press Quick play, which puts you in a game on your own.";

        /// <summary>The menu's note under the two Quickstart entries.</summary>
        public const string MenuNote =
            "Quick play puts you in a game on a PingCore game server, on your own if nobody else is playing. Play on this PC "
            + "hosts a LAN game here; a second player joins it with Direct connect.";

        private const string LogPrefix = "[Quickstart] ";

        /// <summary>How many frames to wait for the client's menu, or for a screen change, before giving up.</summary>
        private const int FramesToWait = 120;

        /// <summary>How long Play on this PC waits for the LAN host to start listening.</summary>
        private const float HostStartSeconds = 10f;

        [SerializeField]
        [Tooltip("The copied Beacon Rush client on the BeaconRushClient object.")]
        private ClientUi clientUi;

        [SerializeField]
        [Tooltip("The client's UIDocument (the Client UI object, switched on by ClientUi for an interactive player).")]
        private UIDocument document;

        private Button quickPlay;
        private Button playLocally;
        private Button originalQuickPlay;
        private Button menuFind;
        private Button findGo;
        private Button menuHost;
        private Button hostLan;
        private Button hostStart;
        private Button hostPlay;
        private bool busy;

        /// <summary>True once the two entries are in the menu.</summary>
        public bool Installed => quickPlay != null;

        /// <summary>Quick play: <see cref="QuickJoinIdleHostedGameServer"/>.</summary>
        public void QuickPlay() => QuickJoinIdleHostedGameServer();

        /// <summary>Opens Find match and queues a ticket in <see cref="BeaconRushProtocol.Queue"/>: a match once a second player is queued.</summary>
        public void FindMatch()
        {
            if (!busy && Installed)
            {
                StartCoroutine(FindMatchRoutine());
            }
        }

        /// <summary>Hosts a LAN-only game on this PC and plays in it: Host a game, LAN only, Start hosting, then Play.</summary>
        public void PlayOnThisPc()
        {
            if (!busy && Installed)
            {
                StartCoroutine(PlayLocallyRoutine());
            }
        }

        /// <summary>
        /// The solo join: presses the copied client's own Quick play (hidden by this component, which shows its own entry
        /// in its place). Discovery's quick join holds a seat on the best fleet game server with room; an idle hosted Beacon
        /// Rush game server admits that reservation alone and, before letting the player in, allocates itself through the
        /// local SDK endpoint (<c>IFleetSdk.AllocateSelfAsync</c>, reached through the SDK's admission pipeline), so it reads
        /// <c>in_session</c> and the matchmaker skips it. The player is admitted on the hold, never on the self-allocation's
        /// id. No seat anywhere falls back to Find match; a game server taken between the hold and the join (a match
        /// landed on it) is tried once more.
        /// </summary>
        public void QuickJoinIdleHostedGameServer()
        {
            if (!busy && Installed && !Press(originalQuickPlay))
            {
                Debug.LogWarning(LogPrefix + "quick join is not available right now");
            }
        }

        private IEnumerator Start()
        {
            if (!ClientLaunch.Interactive)
            {
                enabled = false;
                yield break;
            }

            if (clientUi == null || document == null)
            {
                Debug.LogError(LogPrefix + "QuickstartBootstrap needs the ClientUi and the UIDocument of the scene; reimport the Quickstart sample");
                enabled = false;
                yield break;
            }

            for (int frame = 0; frame < FramesToWait && !TryInstall(); frame++)
            {
                yield return null;
            }

            if (!Installed)
            {
                Debug.LogWarning(LogPrefix + "the Beacon Rush menu did not appear, so the Quickstart entries were not added; its own buttons still work");
                enabled = false;
            }
        }

        private void Update()
        {
            if (!Installed)
            {
                return;
            }

            bool fleetReady = menuFind.enabledSelf;
            quickPlay.SetEnabled(!busy && fleetReady);
            playLocally.SetEnabled(!busy);
        }

        /// <summary>Adds the entries once ClientUi has built its views (it switches the document on in its own Start).</summary>
        private bool TryInstall()
        {
            if (!document.isActiveAndEnabled || document.rootVisualElement == null)
            {
                return false;
            }

            VisualElement root = document.rootVisualElement;
            originalQuickPlay = root.Q<Button>("menu-quick");
            menuFind = root.Q<Button>("menu-find");
            findGo = root.Q<Button>("find-go");
            menuHost = root.Q<Button>("menu-host");
            hostLan = root.Q<Button>("host-lan");
            hostStart = root.Q<Button>("host-start");
            hostPlay = root.Q<Button>("host-play");
            Label findQueue = root.Q<Label>("find-queue");
            if (originalQuickPlay == null || menuFind == null || findGo == null || menuHost == null || hostLan == null || hostStart == null
                || hostPlay == null || findQueue == null || originalQuickPlay.parent == null || findQueue.parent == null)
            {
                return false;
            }

            VisualElement menu = originalQuickPlay.parent;
            int at = menu.IndexOf(originalQuickPlay);
            quickPlay = new Button(QuickPlay) { name = "quickstart-quick", text = "Quick play" };
            quickPlay.AddToClassList("br-button");
            quickPlay.AddToClassList("br-primary");
            playLocally = new Button(PlayOnThisPc) { name = "quickstart-local", text = "Play on this PC" };
            playLocally.AddToClassList("br-button");
            var menuNote = new Label(MenuNote) { name = "quickstart-menu-note" };
            menuNote.AddToClassList("br-hint");
            menu.Insert(at, menuNote);
            menu.Insert(at, playLocally);
            menu.Insert(at, quickPlay);
            originalQuickPlay.style.display = DisplayStyle.None;

            VisualElement findPanel = findQueue.parent;
            var findNote = new Label(SecondPlayerNote) { name = "quickstart-find-note" };
            findNote.AddToClassList("br-hint");
            findPanel.Insert(findPanel.IndexOf(findQueue) + 1, findNote);
            return true;
        }

        private IEnumerator FindMatchRoutine()
        {
            busy = true;
            try
            {
                yield return Open(menuFind, ClientScreen.FindMatch);
                if (clientUi.Screen != ClientScreen.FindMatch)
                {
                    yield break;
                }

                if (!Press(findGo))
                {
                    Debug.LogWarning(LogPrefix + "Find match is busy; press Find match on this screen when it is free");
                }
            }
            finally
            {
                busy = false;
            }
        }

        private IEnumerator PlayLocallyRoutine()
        {
            busy = true;
            try
            {
                yield return Open(menuHost, ClientScreen.Host);
                if (clientUi.Screen != ClientScreen.Host)
                {
                    yield break;
                }

                Press(hostLan);
                yield return null;
                if (!Press(hostStart))
                {
                    yield break;
                }

                // The host screen shows Play once the LAN host listens; a failure (the port in use) shows on that screen.
                float until = Time.realtimeSinceStartup + HostStartSeconds;
                while (Time.realtimeSinceStartup < until && hostPlay.resolvedStyle.display == DisplayStyle.None)
                {
                    yield return null;
                }

                if (hostPlay.resolvedStyle.display != DisplayStyle.None)
                {
                    Press(hostPlay);
                }
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>Presses a menu button and waits until the client shows <paramref name="screen"/>, plus one frame for its refresh.</summary>
        private IEnumerator Open(Button menuButton, ClientScreen screen)
        {
            if (!Press(menuButton))
            {
                Debug.LogWarning(LogPrefix + "the menu button " + menuButton.name + " is not available right now");
                yield break;
            }

            for (int frame = 0; frame < FramesToWait && clientUi.Screen != screen; frame++)
            {
                yield return null;
            }

            if (clientUi.Screen != screen)
            {
                Debug.LogWarning(LogPrefix + "the client did not open " + screen + "; use its menu instead");
                yield break;
            }

            // ClientUi refreshes the newly shown screen (which buttons are enabled) in its next Update.
            yield return null;
        }

        /// <summary>
        /// Clicks <paramref name="button"/> the way a keyboard or gamepad submit does (UI Toolkit buttons click on a
        /// navigation submit), so the copied client's own handler runs. False when the button is disabled.
        /// </summary>
        private static bool Press(Button button)
        {
            if (button == null || !button.enabledInHierarchy)
            {
                return false;
            }

            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }

            return true;
        }
    }
}
