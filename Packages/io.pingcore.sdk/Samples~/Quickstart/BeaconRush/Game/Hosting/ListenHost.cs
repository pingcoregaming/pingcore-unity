using System;
using System.Threading;
using System.Threading.Tasks;
using BeaconRush.Networking;
using BeaconRush.Session;
using PingCore.Discovery.Host;
using UnityEngine;

namespace BeaconRush.Hosting
{
    /// <summary>What <see cref="ListenHost.StartAsync"/> is asked to do.</summary>
    public sealed class ListenHostOptions
    {
        /// <summary>The UDP game port; the reachability echo of an online host listens on the port above it.</summary>
        public ushort Port { get; set; } = ServerArgs.DefaultPort;

        /// <summary>True: no Discovery at all, <c>lan</c> join tickets only. False: online, heartbeating with <c>PINGCORE_DISCOVERY_TOKEN</c>.</summary>
        public bool LanOnly { get; set; }

        /// <summary>Discovery's base URL for an online host (the client settings' URL); null uses <see cref="HostingEnvironment.DefaultDiscoveryUrl"/>.</summary>
        public string DiscoveryUrl { get; set; }

        /// <summary>The listing's name for an online host (1 to 100 characters); null uses <c>Beacon Rush listen host :&lt;port&gt;</c>.</summary>
        public string ListingName { get; set; }

        /// <summary>
        /// An online host's stable listing id (<c>HeartbeatReporterOptions.ServerId</c>, Discovery's server id
        /// pattern), or null to let Discovery list it as <c>ip:port</c>. Whatever Discovery records is what the
        /// <c>heartbeat</c> and <c>delist</c> events report. Ignored LAN only.
        /// </summary>
        public string ServerId { get; set; }

        /// <summary>The host player's own name on the score board.</summary>
        public string HostDisplayName { get; set; }
    }

    /// <summary>How <see cref="ListenHost.StartAsync"/> ended.</summary>
    public enum ListenHostStartOutcome
    {
        /// <summary>Listening (and, online, the first heartbeat sent: see <see cref="ListenHostStartResult.Heartbeat"/>).</summary>
        Started,

        /// <summary>Already hosting; stop first.</summary>
        AlreadyRunning,

        /// <summary>Online was asked for but <c>PINGCORE_DISCOVERY_TOKEN</c> is not set. Nothing was started.</summary>
        NoDiscoveryToken,

        /// <summary>The game port could not be bound (a <c>bootError</c> event says which).</summary>
        ListenFailed,
    }

    /// <summary>The result of <see cref="ListenHost.StartAsync"/>.</summary>
    public sealed class ListenHostStartResult
    {
        public ListenHostStartResult(ListenHostStartOutcome outcome, ushort port, int? queryPort, HeartbeatStartOutcome? heartbeat)
        {
            Outcome = outcome;
            Port = port;
            QueryPort = queryPort;
            Heartbeat = heartbeat;
        }

        public ListenHostStartOutcome Outcome { get; }

        public bool IsStarted => Outcome == ListenHostStartOutcome.Started;

        /// <summary>The game port to forward (UDP).</summary>
        public ushort Port { get; }

        /// <summary>Online: the echo port to forward as well (UDP, game port + 1). Null for LAN only.</summary>
        public int? QueryPort { get; }

        /// <summary>Online: the first heartbeat's outcome. A <c>Failed</c> start keeps retrying in the background; <c>Refused</c> is final. Null for LAN only.</summary>
        public HeartbeatStartOutcome? Heartbeat { get; }
    }

    /// <summary>
    /// A player build that hosts: the listen host mode of <see cref="GameServerRuntime"/>, for the client's Host screen.
    /// Put it beside <see cref="BeaconRushNetwork"/> in the client scene (it brings a <see cref="GameServerRuntime"/>);
    /// the client UI only calls <see cref="StartAsync"/> and <see cref="StopAsync"/> and reads the properties. NGO runs
    /// <c>StartHost</c>, so the host plays too (its player is <see cref="BeaconRushPlayer.Local"/>, and it holds one of the
    /// eight seats). <b>Online</b> lists the host on Discovery with the heartbeat token from the environment variable
    /// <c>PINGCORE_DISCOVERY_TOKEN</c> (read here, never by the UI, never from an argument or an asset), answers the DSCV1
    /// echo on game port + 1, admits <c>reservation</c> join tickets checked with verify, and delists on stop or quit.
    /// <b>LAN only</b> talks to no one and admits <c>lan</c> join tickets only. Sessions are local: a match starts when the
    /// first player is in, and the next one starts after the results with everyone still connected. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BeaconRushNetwork))]
    [RequireComponent(typeof(GameServerRuntime))]
    public sealed class ListenHost : MonoBehaviour
    {
        private GameServerRuntime runtime;
        private HeartbeatTier tier;
        private bool starting;
        private bool ownsRuntime;
        private bool stoppingRaised;

        /// <summary>True when <c>PINGCORE_DISCOVERY_TOKEN</c> is set, so Online can start. The token itself is never exposed.</summary>
        public static bool HasDiscoveryToken => HostingEnvironment.DiscoveryToken() != null;

        /// <summary>True while hosting.</summary>
        public bool IsRunning => runtime != null && runtime.IsRunning;

        /// <summary>True when the current or last start was LAN only.</summary>
        public bool LanOnly { get; private set; }

        /// <summary>The game port of the current or last start.</summary>
        public ushort Port { get; private set; }

        /// <summary>Online: the echo port; null for LAN only.</summary>
        public int? QueryPort => runtime?.QueryPort;

        /// <summary>The session loop (phase, players, time left) for the lobby and match screens.</summary>
        public SessionDirector Director => runtime?.Director;

        /// <summary>Online: what Discovery last said (verification mode, verified, last probe error); null for LAN only or before a start.</summary>
        public HeartbeatStatus HeartbeatStatus => tier?.Reporter.Status;

        /// <summary>Online: the id Discovery listed this host under, or null.</summary>
        public string ServerId => tier?.Reporter.ServerId;

        private void Awake()
        {
            runtime = GetComponent<GameServerRuntime>();
            Application.quitting += OnQuitting;
        }

        /// <summary>Starts hosting. Never throws for a network or Discovery failure; the result says what happened.</summary>
        public async Task<ListenHostStartResult> StartAsync(ListenHostOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (starting || IsRunning)
            {
                return new ListenHostStartResult(ListenHostStartOutcome.AlreadyRunning, Port, QueryPort, null);
            }

            string token = options.LanOnly ? null : HostingEnvironment.DiscoveryToken();
            if (!options.LanOnly && token == null)
            {
                return new ListenHostStartResult(ListenHostStartOutcome.NoDiscoveryToken, options.Port, null, null);
            }

            starting = true;
            try
            {
                LanOnly = options.LanOnly;
                Port = options.Port;
                ownsRuntime = true;
                stoppingRaised = false;
                HostingSelection selection = HostingModeSelector.ForListen(options.LanOnly);
                HostingModeBase mode;
                tier = null;
                if (options.LanOnly)
                {
                    mode = new LanMode(selection, true, options.HostDisplayName);
                }
                else
                {
                    tier = new HeartbeatTier(new HeartbeatReporterOptions
                    {
                        BaseUrl = string.IsNullOrEmpty(options.DiscoveryUrl) ? HostingEnvironment.DefaultDiscoveryUrl : options.DiscoveryUrl,
                        Token = token,
                        Name = string.IsNullOrWhiteSpace(options.ListingName) ? "Beacon Rush listen host :" + options.Port : options.ListingName,
                        GamePort = options.Port,
                        MaxPlayers = BeaconRushProtocol.MaxPlayers,
                        Meta = HeartbeatMeta.Create(),
                        ServerId = string.IsNullOrEmpty(options.ServerId) ? null : options.ServerId,
                    });
                    mode = new HeartbeatMode(selection, tier, true, options.HostDisplayName);
                }

                runtime.LocalSettings = ServerInstrumentation.Ask(i => i.LocalSessionSettings(GameHostingMode.Listen, SessionSettings.Local),
                    SessionSettings.Local) ?? SessionSettings.Local;
                bool listening = await runtime.RunAsync(mode, options.Port, cancellationToken);
                if (!listening && !runtime.IsRunning)
                {
                    await runtime.StopAsync();
                    return new ListenHostStartResult(ListenHostStartOutcome.ListenFailed, options.Port, null, null);
                }

                return new ListenHostStartResult(ListenHostStartOutcome.Started, options.Port, runtime.QueryPort, tier?.LastStartOutcome);
            }
            finally
            {
                starting = false;
            }
        }

        /// <summary>Stops hosting: <c>stopping</c>, no new players, the delist (online) awaited, NGO shut down. Idempotent.</summary>
        public Task StopAsync()
        {
            RaiseStopping();
            return runtime == null ? Task.CompletedTask : runtime.StopAsync();
        }

        private void OnQuitting()
        {
            RaiseStopping();
            runtime?.Stop();
        }

        /// <summary>
        /// The game server's <c>stopping</c> event, once per start, for a host this component started (online or LAN
        /// only), when it stops or the player quits. A runtime another mode runs (a self-hosted <see cref="DedicatedServer"/>
        /// added to the same root) raises its own.
        /// </summary>
        private void RaiseStopping()
        {
            if (!ownsRuntime || stoppingRaised || runtime == null || !runtime.IsRunning)
            {
                return;
            }

            stoppingRaised = true;
            ServerEvents.Raise(ServerEvents.Stopping, "sessionOpen", runtime.Director != null && runtime.Director.HasSession);
        }

        private void OnDestroy()
        {
            Application.quitting -= OnQuitting;
        }
    }
}
