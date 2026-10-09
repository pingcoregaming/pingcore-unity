using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Handshake;
using PingCore.Unity;
using Unity.Netcode;

namespace PingCore.Netcode.NGO
{
    /// <summary>
    /// Netcode for GameObjects connection approval for PingCore join tickets
    /// (https://pingcore.io/docs/fleets/admitting-players). <see cref="Install"/> turns on <c>ConnectionApproval</c>, raises
    /// <c>ClientConnectionBufferTimeout</c> so a pending connection outlives the decision deadline, sets
    /// the approval callback and hooks disconnects. Each connection's callback marks the response
    /// <c>Pending</c>, decides on a later main-thread step (<see cref="AdmissionPipeline"/>: decode, protocol,
    /// stopping, the evidence for the hosting mode, the decision table, the game's gate, the ledger), then
    /// sets <c>Approved</c>, <c>CreatePlayerObject</c> and, on a refusal, <c>Reason</c> (the wire literal NGO
    /// sends the client), and clears <c>Pending</c>. A listen host's own client (<c>ServerClientId</c>) is
    /// approved at once, because NGO reads its answer synchronously. A disconnect gives the seats back
    /// and abandons a pending decision. Use it from the main thread.
    /// </summary>
    public sealed class PingCoreConnectionApproval : IDisposable
    {
        /// <summary>The least <c>ClientConnectionBufferTimeout</c> (seconds) <see cref="Install"/> leaves, so the 10 s decision deadline ends first.</summary>
        public const int MinimumConnectionBufferSeconds = 15;

        private readonly NetworkManager networkManager;
        private readonly ApprovalOptions options;
        private readonly AdmissionPipeline pipeline;
        private readonly Dictionary<ulong, CancellationTokenSource> pending = new Dictionary<ulong, CancellationTokenSource>();
        private bool installed;
        private bool disposed;

        /// <summary>Creates the approval; nothing changes on the NetworkManager until <see cref="Install"/>.</summary>
        /// <param name="networkManager">The game server's (or listen host's) NetworkManager.</param>
        /// <param name="options">Copied. <see cref="ApprovalOptions.Scheduler"/> null uses the Unity main-thread scheduler.</param>
        /// <param name="evidence">The evidence for <see cref="ApprovalOptions.Mode"/>: hosted, heartbeat or LAN.</param>
        /// <param name="gate">The game's own rule; null admits whatever the decision table accepts.</param>
        public PingCoreConnectionApproval(NetworkManager networkManager, ApprovalOptions options, IAdmissionEvidence evidence, IAdmissionGate gate)
        {
            this.networkManager = networkManager != null ? networkManager : throw new ArgumentNullException(nameof(networkManager));
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.options = options.Clone();
            IScheduler scheduler = this.options.Scheduler ?? new AwaitableScheduler();
            pipeline = new AdmissionPipeline(this.options, evidence ?? throw new ArgumentNullException(nameof(evidence)), gate, scheduler);
        }

        /// <summary>
        /// Raised once per decided connection on the main thread: kind, mode, evidence source, verdict,
        /// reason and elapsed time. It carries the ticket's <c>TicketRef</c> only, never the ticket id.
        /// </summary>
        public event Action<AdmissionDecision> Decided;

        /// <summary>The seats held by admitted connections.</summary>
        public AdmissionLedger Ledger => pipeline.Ledger;

        /// <summary>The options it was created with (a copy).</summary>
        public ApprovalOptions Options => options.Clone();

        /// <summary>Connections whose decision is still pending.</summary>
        public int PendingCount => pending.Count;

        /// <summary>
        /// Wires the approval into the NetworkManager: <c>NetworkConfig.ConnectionApproval = true</c>,
        /// <c>ClientConnectionBufferTimeout</c> raised to at least <see cref="MinimumConnectionBufferSeconds"/>
        /// (and 5 s past the deadline), the approval callback set, and the disconnect hook added. Call it
        /// before <c>StartServer</c> or <c>StartHost</c>. Idempotent.
        /// </summary>
        /// <exception cref="InvalidOperationException">The NetworkManager has no NetworkConfig, or another approval callback is installed.</exception>
        public void Install()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(PingCoreConnectionApproval));
            }

            if (installed)
            {
                return;
            }

            NetworkConfig config = networkManager.NetworkConfig;
            if (config == null)
            {
                throw new InvalidOperationException("the NetworkManager has no NetworkConfig");
            }

            Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse> existing = networkManager.ConnectionApprovalCallback;
            if (existing != null)
            {
                throw new InvalidOperationException("another ConnectionApprovalCallback is already installed on this NetworkManager");
            }

            config.ConnectionApproval = true;
            int buffer = Math.Max(MinimumConnectionBufferSeconds, (int)Math.Ceiling(options.Deadline.TotalSeconds) + 5);
            if (config.ClientConnectionBufferTimeout < buffer)
            {
                config.ClientConnectionBufferTimeout = buffer;
            }

            networkManager.ConnectionApprovalCallback = OnApproval;
            networkManager.OnClientDisconnectCallback += OnClientDisconnect;
            installed = true;
        }

        /// <summary>From now on every connection is refused as <c>stopping</c>. Call it from <c>Application.quitting</c>. Idempotent.</summary>
        public void NotifyStopping() => pipeline.NotifyStopping();

        /// <summary>Removes the callback and the disconnect hook and abandons pending decisions. The ledger is kept. Idempotent.</summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (installed && networkManager != null)
            {
                if (networkManager.ConnectionApprovalCallback == OnApproval)
                {
                    networkManager.ConnectionApprovalCallback = null;
                }

                networkManager.OnClientDisconnectCallback -= OnClientDisconnect;
            }

            foreach (CancellationTokenSource source in pending.Values)
            {
                source.Cancel();
            }

            pending.Clear();
        }

        private void OnApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            ulong clientId = request.ClientNetworkId;
            if (clientId == NetworkManager.ServerClientId)
            {
                // A listen host's own client: NGO reads this answer synchronously and cannot decline it.
                response.Approved = true;
                response.CreatePlayerObject = options.CreatePlayerObject;
                response.Pending = false;
                Raise(AdmissionDecision.HostClient(options.Mode, clientId));
                return;
            }

            if (disposed)
            {
                response.Approved = false;
                response.Reason = JoinRejectReasons.ToWire(JoinRejectReason.Stopping);
                response.Pending = false;
                return;
            }

            response.Pending = true;
            byte[] payload = request.Payload == null ? null : (byte[])request.Payload.Clone();
            if (pending.TryGetValue(clientId, out CancellationTokenSource previous))
            {
                previous.Cancel();
            }

            var source = new CancellationTokenSource();
            pending[clientId] = source;
            _ = DecideAsync(clientId, payload, response, source);
        }

        private async Task DecideAsync(ulong clientId, byte[] payload, NetworkManager.ConnectionApprovalResponse response, CancellationTokenSource source)
        {
            AdmissionDecision decision;
            try
            {
                // Always answer on a later step, never inside NGO's message handler.
                await Task.Yield();
                decision = await pipeline.AdmitAsync(clientId, payload, source.Token);
            }
            catch (Exception e)
            {
                decision = AdmissionDecision.Reject(JoinRejectReason.RefusedByGame, "the approval failed (" + e.GetType().Name + ")", options.Mode, null).For(clientId, 0);
            }

            bool abandoned = source.IsCancellationRequested;
            if (pending.TryGetValue(clientId, out CancellationTokenSource current) && current == source)
            {
                pending.Remove(clientId);
            }

            source.Dispose();
            if (abandoned)
            {
                // The connection closed (or this approval was disposed) while pending: give back any seat.
                if (decision.Approved)
                {
                    pipeline.Release(clientId);
                    decision = decision.AsRejection(JoinRejectReason.ApprovalTimeout, "the connection closed before the decision was delivered");
                }
            }

            response.Approved = decision.Approved;
            response.CreatePlayerObject = decision.Approved && options.CreatePlayerObject;
            response.Reason = decision.Approved ? null : decision.ReasonWire;
            response.Pending = false;
            Raise(decision);
        }

        private void OnClientDisconnect(ulong clientId)
        {
            if (pending.TryGetValue(clientId, out CancellationTokenSource source))
            {
                pending.Remove(clientId);
                source.Cancel();
            }

            pipeline.Release(clientId);
        }

        private void Raise(AdmissionDecision decision)
        {
            try
            {
                Decided?.Invoke(decision);
            }
            catch (Exception)
            {
                // A subscriber's failure must not break the approval.
            }
        }
    }
}
