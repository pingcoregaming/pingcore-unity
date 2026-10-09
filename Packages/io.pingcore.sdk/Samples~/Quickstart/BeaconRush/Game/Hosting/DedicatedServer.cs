using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeaconRush.Networking;
using PingCore.Discovery.Host;
using PingCore.Fleet;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The Beacon Rush dedicated server's boot, the one object in <c>Scenes/Server.unity</c> (with
    /// <see cref="BeaconRushNetwork"/> and <see cref="GameServerRuntime"/>). Boot sequence:
    /// <list type="number">
    /// <item>Parse <c>-port</c> (<see cref="ServerArgs"/>) and create the local SDK shim, which is hosted only when the
    /// supervisor exported <c>AGONES_SDK_HTTP_PORT</c>. Raise <c>boot</c>.</item>
    /// <item>Hosted: <c>StartAsync</c> (GETs only). Then pick the mode (<see cref="HostingModeSelector"/>): the endpoint
    /// answered is hosted; configured but silent quits with 1; otherwise <c>PINGCORE_DISCOVERY_TOKEN</c> set is
    /// self-hosted, and neither is unlisted.</item>
    /// <item>Run the <see cref="GameServerRuntime"/> in that mode: hosted writes <c>players = 0</c> first (the deliberate
    /// integrating write, deferred until after a 2xx <c>/ready</c> when the installed <see cref="ServerInstrumentation"/>
    /// asks for it, <see cref="BootPlan"/>), listens, awaits <see cref="ServerInstrumentation.BeforeReadyAsync"/> and calls
    /// <c>ReadyAsync</c>; self-hosted listens, binds the reachability echo on game port + 1 and heartbeats; unlisted only
    /// listens. Every <c>players</c> write is reported to <see cref="ServerInstrumentation.PlayersWriteReturned"/> as it returns.</item>
    /// <item><c>Application.quitting</c> (an orderly quit, or SIGTERM from the supervisor): <c>stopping</c>, the shim is told
    /// the process is stopping (hosted) or the delist starts (self-hosted), NGO shuts down, <c>exit</c>.</item>
    /// </list>
    /// The game server identity is the shim's <c>GET /gameserver</c> name on a hosted game server; nothing here reads
    /// the platform's own environment or files.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BeaconRushNetwork))]
    [RequireComponent(typeof(GameServerRuntime))]
    public sealed class DedicatedServer : MonoBehaviour
    {
        public const string PlayersCounter = PlayersCounterWriter.Counter;

        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Stopwatch sinceBoot = Stopwatch.StartNew();
        private readonly FleetEventBridge bridge = new FleetEventBridge();

        private GameServerRuntime runtime;
        private IFleetSdk fleet;
        private int exitCode;
        private bool stopping;
        private bool exited;

        /// <summary>The local SDK shim, or null before boot.</summary>
        public IFleetSdk Fleet => fleet;

        /// <summary>The game server runtime.</summary>
        public GameServerRuntime Runtime => runtime;

        /// <summary>
        /// Self-hosted only: the stable id to list this game server under (<c>HeartbeatReporterOptions.ServerId</c>,
        /// Discovery's server id pattern), or null to let Discovery list it as <c>ip:port</c>. Part of the game's own
        /// configuration, never a platform variable; set it before the first frame (code that adds this component at run
        /// time sets it first). Ignored on a hosted game server, whose identity is the local SDK endpoint's.
        /// </summary>
        public string SelfHostedServerId { get; set; }

        private void Awake()
        {
            // A headless game server otherwise spins a core.
            Application.targetFrameRate = BeaconRushProtocol.TickRate;
            QualitySettings.vSyncCount = 0;
            Application.quitting += OnQuitting;
            runtime = GetComponent<GameServerRuntime>();
        }

        private async void Start()
        {
            try
            {
                await Boot();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                ServerEvents.Raise(ServerEvents.BootError, "reason", "exception", "detail", e.GetType().Name);
                Quit(1);
            }
        }

        private async Task Boot()
        {
            CancellationToken token = lifetime.Token;
            ServerArgs args = ServerArgs.Parse(Environment.GetCommandLineArgs());
            if (!args.IsValid)
            {
                Debug.LogError("[BeaconRush] " + args.Error);
                ServerEvents.Raise(ServerEvents.BootError, "reason", "bad_arguments", "detail", args.Error);
                Quit(2);
                return;
            }

            fleet = FleetSdk.Create(new FleetSdkOptions { Log = FleetEventBridge.OnFleetLog });
            ServerEvents.Raise(ServerEvents.Boot,
                "processId", ProcessIdentity.NewProcessId(),
                "hosting", fleet.IsHosted ? "hosted" : "inert",
                "port", (int)args.Port,
                "portSource", args.PortSource,
                "unityVersion", Application.unityVersion,
                "buildVersion", BuildVersionFile.Read(),
                "protocolVersion", BeaconRushProtocol.Version);

            bool answered = false;
            if (fleet.IsHosted)
            {
                bridge.Attach(fleet);
                answered = await fleet.StartAsync(token);
                if (stopping || token.IsCancellationRequested)
                {
                    return;
                }
            }

            HostingSelection selection = HostingModeSelector.Select(fleet.IsHosted, answered, HostingEnvironment.DiscoveryToken());
            if (selection.Failed)
            {
                ServerEvents.Raise(ServerEvents.SdkError, "call", "start", "outcome", FleetEventBridge.Wire(FleetCallOutcome.Unreachable), "status", 0);
                Quit(1);
                return;
            }

            HostingModeBase mode = CreateMode(selection, args.Port, token);
            if (!await runtime.RunAsync(mode, args.Port, token) && !stopping)
            {
                // The runtime raised bootError (the port could not be bound).
                Quit(1);
            }
        }

        private HostingModeBase CreateMode(HostingSelection selection, ushort port, CancellationToken token)
        {
            switch (selection.Mode)
            {
                case GameHostingMode.Hosted:
                {
                    var counter = new PlayersCounterWriter(fleet, () => runtime.Director.Players, () => stopping,
                        new PingCore.Unity.AwaitableScheduler(), token,
                        outcome => ServerInstrumentation.Tell(i => i.PlayersWriteReturned(outcome)));
                    BootPlan plan = BootPlan.For(ServerInstrumentation.Ask(i => i.DeferFirstPlayersWrite, false));
                    return new HostedMode(selection, fleet, counter, plan, sinceBoot);
                }

                case GameHostingMode.SelfHosted:
                    return new HeartbeatMode(selection, new HeartbeatTier(HeartbeatOptions(port, SelfHostedServerId)), false, null);
                default:
                    return new LanMode(selection, false, null);
            }
        }

        private static HeartbeatReporterOptions HeartbeatOptions(ushort port, string serverId)
        {
            return new HeartbeatReporterOptions
            {
                BaseUrl = HostingEnvironment.DefaultDiscoveryUrl,
                Token = HostingEnvironment.DiscoveryToken(),
                Name = "Beacon Rush dedicated :" + port,
                GamePort = port,
                MaxPlayers = BeaconRushProtocol.MaxPlayers,
                Version = BuildVersionFile.Read(),
                Meta = HeartbeatMeta.Create(),
                ServerId = string.IsNullOrEmpty(serverId) ? null : serverId,
            };
        }

        private void Quit(int code)
        {
            exitCode = code;
            Application.Quit(code);
        }

        /// <summary>
        /// An orderly stop before the process quits (for code that hosts this component and is asked to stop):
        /// <c>stopping</c>, no new players, the self-hosted delist awaited, NGO shut down. The process still quits
        /// afterwards, which raises <c>exit</c>. Idempotent.
        /// </summary>
        public Task StopAsync()
        {
            if (!BeginStopping())
            {
                return Task.CompletedTask;
            }

            // The runtime's own stop first (it awaits the delist), then the boot's token.
            Task stopped = runtime == null ? Task.CompletedTask : runtime.StopAsync();
            lifetime.Cancel();
            fleet?.NotifyProcessStopping();
            return stopped;
        }

        private bool BeginStopping()
        {
            if (stopping)
            {
                return false;
            }

            stopping = true;
            ServerEvents.Raise(ServerEvents.Stopping, "sessionOpen", runtime != null && runtime.Director.HasSession);
            return true;
        }

        private void OnQuitting()
        {
            if (exited)
            {
                return;
            }

            exited = true;
            BeginStopping();
            lifetime.Cancel();
            runtime?.Stop();

            // Also when the boot stopped before the runtime ran (idempotent).
            fleet?.NotifyProcessStopping();

            ServerEvents.Raise(ServerEvents.Exit, "code", exitCode);
        }

        private void OnDestroy()
        {
            Application.quitting -= OnQuitting;
            if (fleet != null)
            {
                bridge.Detach();
                fleet.Dispose();
            }

            // The token source is cancelled, never disposed: a continuation still running may read its token.
            lifetime.Cancel();
        }
    }
}
