using System;
using System.Threading;
using BeaconRush.Client.Flows;
using BeaconRush.Client.Models;
using BeaconRush.Client.UI.Views;
using BeaconRush.Hosting;
using BeaconRush.Networking;
using PingCore.Core.Discovery;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI
{
    /// <summary>The screens of the client.</summary>
    public enum ClientScreen
    {
        Home,
        Browse,
        FindMatch,
        JoinReservation,
        Host,
        DirectConnect,
        Connecting,
        Lobby,
        Match,
        Results,
    }

    /// <summary>
    /// The interactive Beacon Rush client on UI Toolkit: it owns the flows (Discovery calls, the connection, the listen
    /// host) and the pure view models (<see cref="BrowserModel"/>, <see cref="MatchmakingModel"/>, <see cref="LobbyModel"/>,
    /// <see cref="HostModel"/>), and <see cref="ClientViews"/> draws them from <c>Layout/ClientMenu.uxml</c>: the main menu
    /// (Quick play, Find match, Browse, Host, Direct connect, Join by game server id), the HUD (scores, timer, phase), the
    /// results screen and the status line, where refusals read as plain sentences (<see cref="ConnectionText"/>), and the
    /// missing-infrastructure banner the SDK's check fills at launch (<see cref="InfrastructureBanner"/>). The
    /// <see cref="UIDocument"/> sits on an inactive object and is switched on only for an interactive player, so batchmode
    /// and a headless process never build a panel. The partial files hold the flows.
    /// </summary>
    public sealed partial class ClientUi : MonoBehaviour
    {
        private const string DisplayNameKey = "beaconrush.displayName";
        private static readonly TimeSpan LobbyTimeout = TimeSpan.FromSeconds(30);

        [SerializeField]
        [Tooltip("The UIDocument of the client UI, on an inactive object; switched on only for an interactive player.")]
        private UIDocument document;

        private ClientBootstrap boot;
        private ClientServices services;
        private BrowserModel browser;
        private MatchmakingModel matchmaking;
        private LobbyModel lobby;
        private HostModel host;
        private ClientViews views;
        private ClientScreen screen = ClientScreen.Home;
        private ClientScreen returnScreen = ClientScreen.Home;
        private string displayName;
        private GameConnection connection;
        private ListenHost listenHost;
        private GameServerRuntime hostRuntime;
        private CancellationTokenSource lifetime;
        private string lanPlayerId;
        private ProfileSlotLocks profileSlots;
        private string profile;
        private InteractiveProfile.Source profileSource;

        /// <summary>The NetworkManager of the session on screen (a client connection or this player's own host), or null.</summary>
        public NetworkManager ActiveManager
        {
            get
            {
                if (connection != null && connection.State == ConnectionState.Connected)
                {
                    return connection.Manager;
                }

                return listenHost != null && listenHost.IsRunning ? hostRuntime.Network : null;
            }
        }

        /// <summary>The screen on show.</summary>
        public ClientScreen Screen => screen;

        internal ClientServices Services => services;

        internal BrowserModel Browser => browser;

        internal MatchmakingModel Matchmaking => matchmaking;

        internal LobbyModel Lobby => lobby;

        internal HostModel Host => host;

        /// <summary>The line under the screen (progress or a refusal), or null.</summary>
        internal string Notice { get; private set; }

        /// <summary>True when <see cref="Notice"/> reports a failure.</summary>
        internal bool NoticeIsError { get; private set; }

        /// <summary>What the Connecting screen says.</summary>
        internal string ConnectingText { get; private set; }

        /// <summary>The player's display name (letters, digits, space, <c>_ . -</c>; at most 32).</summary>
        internal string DisplayName
        {
            get => displayName;
            set
            {
                string cleaned = SanitizeName(value);
                if (cleaned != displayName)
                {
                    displayName = cleaned;
                    PlayerPrefs.SetString(DisplayNameKey, displayName);
                }
            }
        }

        internal static DateTimeOffset Now => DateTimeOffset.UtcNow;

        private void Start()
        {
            if (!ClientLaunch.Interactive)
            {
                enabled = false;
                return;
            }

            boot = GetComponent<ClientBootstrap>();
            listenHost = GetComponent<ListenHost>();
            hostRuntime = GetComponent<GameServerRuntime>();
            if (boot == null || boot.Settings == null || boot.Network == null || listenHost == null || hostRuntime == null || document == null)
            {
                Debug.LogError("[ClientUi] the client scene is incomplete; regenerate it with BeaconRush.Editor.ClientSceneBuilder");
                enabled = false;
                return;
            }

            lifetime = new CancellationTokenSource();
            // One player per copy of the game: -pingcoreProfile, else this copy's instance slot (InteractiveProfile).
            profileSlots = new ProfileSlotLocks(Application.persistentDataPath);
            if (!ProfileSlotLocks.Supported)
            {
                Debug.Log("[ClientUi] " + ProfileSlotLocks.UnsupportedNote);
            }

            string virtualPlayer = InteractiveProfile.FromVirtualPlayer(VirtualPlayerTags.IsAdditionalEditor, VirtualPlayerTags.Tags, VirtualPlayerTags.InstanceId);
            profile = InteractiveProfile.Resolve(InteractiveProfile.FromArgs(Environment.GetCommandLineArgs()), virtualPlayer, profileSlots.TryClaim, null, out profileSource);
            Debug.Log("[ClientUi] player profile " + ProfileLabel);
            services = boot.CreateServices(new ClientServicesOptions { Profile = profile, Log = entry => Debug.Log("[Discovery] " + entry) });
            browser = new BrowserModel(BeaconRushProtocol.Version);
            matchmaking = new MatchmakingModel(BeaconRushProtocol.Version);
            lobby = new LobbyModel();
            host = new HostModel(ListenHost.HasDiscoveryToken);
            displayName = SanitizeName(PlayerPrefs.GetString(DisplayNameKey, "Player"));
            lanPlayerId = "lan-" + SecureIds.NewId128().Substring(0, 12);

            // The panel exists only from here on: an interactive player, never batchmode or a headless process.
            document.gameObject.SetActive(true);
            views = new ClientViews(document.rootVisualElement, this);
            views.Show(screen);

            // Before any join: say which part of the PingCore backend is missing, if any (the menu shows it).
            CheckInfrastructure();
        }

        private void Update()
        {
            connection?.Tick();
            TrackSession();
            if (views != null)
            {
                views.Show(screen);
                views.Refresh(Now);
            }
        }

        private void OnDestroy()
        {
            lifetime?.Cancel();
            DisposeTicket();
            connection?.Dispose();
            services?.Dispose();
            profileSlots?.Dispose();
        }

        /// <summary>Shows a menu screen; Browse starts the latency measurement its latency column uses.</summary>
        internal void Navigate(ClientScreen target)
        {
            ClearNotice();
            screen = target;
            if (target == ClientScreen.Browse)
            {
                BeginLatency();
            }
        }

        /// <summary>Back to the main menu.</summary>
        internal void Back() => Navigate(ClientScreen.Home);

        internal void QuitGame() => Application.Quit();

        internal void ClearNotice()
        {
            Notice = null;
            NoticeIsError = false;
        }

        /// <summary>The player profile, as the menu shows it.</summary>
        internal string ProfileLabel
        {
            get
            {
                string name = string.IsNullOrEmpty(profile) ? "default" : profile;
                switch (profileSource)
                {
                    case InteractiveProfile.Source.Argument: return name + " (" + InteractiveProfile.Flag + ")";
                    case InteractiveProfile.Source.VirtualPlayer: return name + " (Multiplayer Play Mode player tag)";
                    case InteractiveProfile.Source.Session: return name + " (this session only: every instance slot is in use)";
                    default: return name;
                }
            }
        }

        private void Progress(string text)
        {
            Notice = text;
            NoticeIsError = false;
        }

        private void Fail(string text)
        {
            Notice = text;
            NoticeIsError = true;
        }

        private static string SanitizeName(string typed)
        {
            var kept = new System.Text.StringBuilder();
            foreach (char c in typed ?? string.Empty)
            {
                if (kept.Length < 32 && (char.IsLetterOrDigit(c) && c < 128 || c == ' ' || c == '_' || c == '.' || c == '-'))
                {
                    kept.Append(c);
                }
            }

            return kept.Length == 0 ? "Player" : kept.ToString();
        }

        private string JoinName => string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
    }
}
