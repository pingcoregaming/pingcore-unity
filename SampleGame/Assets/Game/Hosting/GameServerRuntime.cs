using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeaconRush.Admission;
using BeaconRush.Match;
using BeaconRush.Networking;
using BeaconRush.Session;
using PingCore.Core.Handshake;
using PingCore.Netcode.NGO;
using Unity.Netcode;
using UnityEngine;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The game server side of Beacon Rush in every hosting mode: a dedicated game server
    /// (<see cref="DedicatedServer"/>) and a listen host inside the player (<see cref="ListenHost"/>) both run one.
    /// It creates the NGO server from <see cref="BeaconRushNetwork"/>, installs the SDK's
    /// <see cref="PingCoreConnectionApproval"/> with the mode's evidence and the game's
    /// <see cref="BeaconRushAdmissionGate"/>, listens (<c>StartServer</c>, or <c>StartHost</c> for a listen host), runs
    /// the <see cref="SessionDirector"/> and the <see cref="MatchController"/>, and hands what differs between modes to
    /// the <see cref="HostingModeBase"/>. Hosted sessions are allocations; every other mode opens local sessions
    /// (<c>local-&lt;8 hex&gt;</c>) itself, one after the other, keeping its players connected between them. The NGO
    /// handlers are in <c>GameServerRuntime.Connections.cs</c>, the session side in <c>GameServerRuntime.Session.cs</c>.
    /// Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BeaconRushNetwork))]
    public sealed partial class GameServerRuntime : MonoBehaviour
    {
        private readonly SessionDirector director = new SessionDirector();
        private readonly HashSet<ulong> connectedClients = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> displayNames = new Dictionary<ulong, string>();
        private HostingModeBase mode;
        private NetworkManager network;
        private PingCoreConnectionApproval approval;
        private BeaconRushAdmissionGate gate;
        private MatchController match;
        private CancellationTokenSource lifetime;
        private bool stopping;

        /// <summary>The session loop.</summary>
        public SessionDirector Director => director;

        /// <summary>The NGO manager while running, else null.</summary>
        public NetworkManager Network => network;

        /// <summary>The hosting mode while running.</summary>
        public GameHostingMode? Mode => mode?.Mode;

        /// <summary>True from a successful listen until stopped.</summary>
        public bool IsRunning => network != null && network.IsListening && !stopping;

        /// <summary>The UDP port of the reachability echo, or null without a heartbeat.</summary>
        public int? QueryPort => mode?.QueryPort;

        /// <summary>
        /// Starts the game server on <paramref name="port"/> in <paramref name="hostingMode"/>. Raises <c>hosting</c>, then
        /// <c>listening</c>; false when it could not listen (a <c>bootError</c> was raised) or it stopped first.
        /// </summary>
        internal async Task<bool> RunAsync(HostingModeBase hostingMode, ushort port, CancellationToken cancellationToken)
        {
            if (mode != null && !stopping)
            {
                throw new InvalidOperationException("this runtime is already running; stop it first");
            }

            Cleanup();
            mode = hostingMode ?? throw new ArgumentNullException(nameof(hostingMode));
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = lifetime.Token;
            mode.Attach(this);
            ApprovalOptions approvalOptions = mode.ApprovalOptions();
            ServerEvents.Raise(ServerEvents.Hosting,
                "mode", HostingModeSelector.Wire(mode.Mode),
                "reason", mode.Selection.Reason,
                "lanOnly", mode.Selection.LanOnly,
                "port", (int)port,
                "queryPort", mode.QueryPort,
                "evidence", mode.Evidence.Source,
                "selfAllocatedJoins", approvalOptions.AllowSelfAllocatedJoins);

            if (!await mode.BeforeListeningAsync(token) || stopping)
            {
                return false;
            }

            BeaconRushNetwork prefabs = GetComponent<BeaconRushNetwork>();
            network = prefabs.CreateServer(port);
            gate = new BeaconRushAdmissionGate(GateSnapshot);
            approval = new PingCoreConnectionApproval(network, approvalOptions, mode.Evidence, gate);
            approval.Decided += OnDecided;
            approval.Install();
            network.OnClientConnectedCallback += OnClientConnected;
            network.OnClientDisconnectCallback += OnClientDisconnected;
            if (!mode.UsesAllocations)
            {
                // Open before listening: a listen host's own client connects inside StartHost.
                OpenLocalSession();
            }

            bool started = mode.HostPlays ? network.StartHost() : network.StartServer();
            if (!started)
            {
                ServerEvents.Raise(ServerEvents.BootError, "reason", "listen_failed", "detail", "UDP port " + port + " could not be bound");
                return false;
            }

            ServerEvents.Raise(ServerEvents.Listening, "port", (int)port, "transport", "udp");
            match = new MatchController(network, prefabs.BeaconPrefab, prefabs.ScoreBoardPrefab);
            match.SpawnBoard();
            foreach (ulong clientId in director.SessionPlayers)
            {
                match.AddPlayer(clientId, NameOf(clientId));
            }

            if (director.HasSession)
            {
                match.OnPhase(true, director.Phase, director.TimeLeft);
            }

            await mode.AfterListeningAsync(token);
            return !stopping;
        }

        /// <summary>
        /// Stops taking players and tells the mode (ready to delist, the shim told the process is stopping), then shuts
        /// NGO down. Called from <c>Application.quitting</c>, or by a listen host. Idempotent.
        /// </summary>
        internal void Stop()
        {
            if (stopping)
            {
                return;
            }

            stopping = true;
            approval?.NotifyStopping();
            mode?.NotifyStopping();
            lifetime?.Cancel();
            ShutdownNetwork();
        }

        /// <summary>As <see cref="Stop"/>, waiting for the mode's own stop (the delist) first.</summary>
        internal async Task StopAsync()
        {
            if (stopping)
            {
                return;
            }

            stopping = true;
            approval?.NotifyStopping();
            lifetime?.Cancel();
            Task modeStop = mode == null ? Task.CompletedTask : mode.StopAsync();
            ShutdownNetwork();
            try
            {
                await modeStop;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void Update()
        {
            if (stopping || mode == null || network == null || !network.IsListening)
            {
                return;
            }

            float seconds = Time.unscaledDeltaTime;
            if (director.HasSession)
            {
                AfterInput(director.Tick(TimeSpan.FromSeconds(seconds)));
            }

            if (director.HasSession && match != null
                && match.Tick(seconds, director.Phase, director.TimeLeft, director.Settings.Bots))
            {
                AfterInput(director.OnScoreLimitReached());
            }
        }

        private GateState GateSnapshot()
        {
            bool open = director.HasSession && mode.IsCurrentSession(director.AllocationId);
            int seats = (approval == null ? 0 : approval.Ledger.Count) + (mode.HostPlays ? 1 : 0);
            return new GateState(mode.Mode, open, director.Phase, seats, director.HasRoster);
        }

        private void ShutdownNetwork()
        {
            if (network == null)
            {
                return;
            }

            network.OnClientConnectedCallback -= OnClientConnected;
            network.OnClientDisconnectCallback -= OnClientDisconnected;
            if (network.IsListening)
            {
                network.Shutdown();
            }
        }

        private void OnDestroy() => Cleanup();

        /// <summary>Releases what the last run held, so a stopped runtime can run again (a listen host hosting a second time).</summary>
        private void Cleanup()
        {
            if (approval != null)
            {
                approval.Decided -= OnDecided;
                approval.Dispose();
                approval = null;
            }

            mode?.Dispose();
            mode = null;

            // The token source is cancelled, never disposed: a continuation still running may read its token.
            lifetime?.Cancel();
            lifetime = null;
            ShutdownNetwork();
            if (network != null)
            {
                Destroy(network.gameObject);
                network = null;
            }

            match = null;
            gate = null;
            director.OnAllocationCleared();
            connectedClients.Clear();
            displayNames.Clear();
            lastPhaseVersion = director.PhaseVersion;
            stopping = false;
        }
    }
}
