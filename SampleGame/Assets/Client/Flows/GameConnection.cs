using System;
using BeaconRush.Client.Models;
using BeaconRush.Networking;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BeaconRush.Client.Flows
{
    /// <summary>Where a client connection is.</summary>
    public enum ConnectionState
    {
        /// <summary>Not started.</summary>
        Idle,

        /// <summary>NGO is connecting and the game server is deciding.</summary>
        Connecting,

        /// <summary>Approved and connected.</summary>
        Connected,

        /// <summary>Refused, timed out or never started; never connected.</summary>
        Rejected,

        /// <summary>Was connected; the connection ended.</summary>
        Left,
    }

    /// <summary>
    /// One NGO client connection to a Beacon Rush game server: the join ticket bytes go into
    /// <c>NetworkConfig.ConnectionData</c>, <c>ClientConnectionBufferTimeout</c> is 15 s (above the game
    /// server's 10 s decision deadline), and the outcome is approved, rejected with the game server's reject
    /// literal (NGO's disconnect reason), <c>connect_timeout</c> after <see cref="ConnectTimeoutSeconds"/>, or
    /// left. Main thread only; call <see cref="Tick"/> every frame.
    /// </summary>
    public sealed class GameConnection : IDisposable
    {
        /// <summary>How long a connection may stay unanswered.</summary>
        public const float ConnectTimeoutSeconds = 20f;

        /// <summary>NGO's pending-connection timeout the client sets, above the game server's 10 s decision deadline.</summary>
        public const int ConnectionBufferSeconds = 15;

        private readonly BeaconRushNetwork network;
        private GameObject closing;
        private float startedAt;
        private float connectedAt = -1f;

        /// <summary>Creates a connection that builds its NetworkManager from <paramref name="network"/>.</summary>
        public GameConnection(BeaconRushNetwork network)
        {
            this.network = network != null ? network : throw new ArgumentNullException(nameof(network));
        }

        /// <summary>Raised once when the game server approved the connection.</summary>
        public event Action<GameConnection> Approved;

        /// <summary>Raised once when the connection never got in (<see cref="Reason"/> says why).</summary>
        public event Action<GameConnection> Rejected;

        /// <summary>Raised once when an approved connection ended (<see cref="Reason"/> says why).</summary>
        public event Action<GameConnection> Left;

        /// <summary>Where the connection is.</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Idle;

        /// <summary>The NGO client, once started.</summary>
        public NetworkManager Manager { get; private set; }

        /// <summary>Where it connects.</summary>
        public GameEndpoint Endpoint { get; private set; }

        /// <summary>The reject literal, <c>connect_timeout</c>, <c>start_failed</c>, <c>disconnected</c>, <c>left</c> or the game server's disconnect reason.</summary>
        public string Reason { get; private set; }

        /// <summary>
        /// True once the NGO client has finished shutting down (or never started). A leaving client must stay alive
        /// until then, so its disconnect reaches the game server instead of the game server timing it out 30 s later.
        /// </summary>
        public bool IsClosed => Manager == null && closing == null;

        /// <summary>The NGO client id once connected.</summary>
        public ulong ClientId { get; private set; }

        /// <summary>Milliseconds from start to approval.</summary>
        public long ConnectMs => connectedAt < 0f ? 0 : (long)((connectedAt - startedAt) * 1000f);

        /// <summary>Milliseconds connected so far.</summary>
        public long ConnectedMs => connectedAt < 0f ? 0 : (long)((Time.realtimeSinceStartup - connectedAt) * 1000f);

        /// <summary>Starts the NGO client with <paramref name="payload"/> as the connection data. False when NGO would not start.</summary>
        public bool Start(GameEndpoint endpoint, byte[] payload)
        {
            if (State != ConnectionState.Idle)
            {
                throw new InvalidOperationException("a GameConnection is used once");
            }

            Endpoint = endpoint;
            Manager = network.CreateClient(endpoint.Address, endpoint.Port, payload);
            Manager.NetworkConfig.ClientConnectionBufferTimeout = ConnectionBufferSeconds;
            Manager.OnClientConnectedCallback += OnConnected;
            Manager.OnClientDisconnectCallback += OnDisconnected;
            startedAt = Time.realtimeSinceStartup;
            State = ConnectionState.Connecting;
            if (!Manager.StartClient())
            {
                Finish(ConnectionState.Rejected, "start_failed");
                return false;
            }

            return true;
        }

        /// <summary>Times out an unanswered connection.</summary>
        public void Tick()
        {
            if (State == ConnectionState.Connecting && Time.realtimeSinceStartup - startedAt > ConnectTimeoutSeconds)
            {
                Finish(ConnectionState.Rejected, "connect_timeout");
            }
        }

        /// <summary>Leaves (or abandons a pending connection) and shuts the NGO client down.</summary>
        public void Leave()
        {
            if (State == ConnectionState.Connected)
            {
                Finish(ConnectionState.Left, "left");
            }
            else if (State == ConnectionState.Connecting)
            {
                Finish(ConnectionState.Rejected, "left");
            }
            else
            {
                ShutdownManager();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Approved = null;
            Rejected = null;
            Left = null;
            Leave();
        }

        private void OnConnected(ulong clientId)
        {
            if (State != ConnectionState.Connecting || Manager == null || clientId != Manager.LocalClientId)
            {
                return;
            }

            connectedAt = Time.realtimeSinceStartup;
            ClientId = clientId;
            State = ConnectionState.Connected;
            Raise(Approved);
        }

        private void OnDisconnected(ulong clientId)
        {
            if (Manager == null || (clientId != Manager.LocalClientId && clientId != NetworkManager.ServerClientId))
            {
                return;
            }

            string reason = string.IsNullOrEmpty(Manager.DisconnectReason) ? "disconnected" : Manager.DisconnectReason;
            if (State == ConnectionState.Connecting)
            {
                Finish(ConnectionState.Rejected, reason);
            }
            else if (State == ConnectionState.Connected)
            {
                Finish(ConnectionState.Left, reason);
            }
        }

        private void Finish(ConnectionState state, string reason)
        {
            ConnectionState previous = State;
            State = state;
            Reason = reason;
            ShutdownManager();
            if (state == ConnectionState.Rejected && previous == ConnectionState.Connecting)
            {
                Raise(Rejected);
            }
            else if (state == ConnectionState.Left && previous == ConnectionState.Connected)
            {
                Raise(Left);
            }
        }

        private void ShutdownManager()
        {
            NetworkManager manager = Manager;
            if (manager == null)
            {
                return;
            }

            manager.OnClientConnectedCallback -= OnConnected;
            manager.OnClientDisconnectCallback -= OnDisconnected;
            Manager = null;
            if (manager.IsListening || manager.ShutdownInProgress)
            {
                // Shutdown sends the disconnect over the next frames; the reaper destroys the object once it is done.
                manager.Shutdown();
                closing = manager.gameObject;
                closing.AddComponent<ShutdownReaper>();
                return;
            }

            Object.Destroy(manager.gameObject);
        }

        private void Raise(Action<GameConnection> handler)
        {
            try
            {
                handler?.Invoke(this);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }

    /// <summary>Destroys a NetworkManager's object once its shutdown has finished, or after 3 s at most.</summary>
    internal sealed class ShutdownReaper : MonoBehaviour
    {
        private const float MaxSeconds = 3f;
        private NetworkManager manager;
        private float startedAt;

        private void Awake()
        {
            manager = GetComponent<NetworkManager>();
            startedAt = Time.realtimeSinceStartup;
        }

        private void LateUpdate()
        {
            if (manager == null || !manager.ShutdownInProgress || Time.realtimeSinceStartup - startedAt > MaxSeconds)
            {
                Destroy(gameObject);
            }
        }
    }
}
