using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace BeaconRush.Networking
{
    /// <summary>
    /// Builds the NGO <see cref="NetworkManager"/> at runtime, configured identically for the game
    /// server, the listen host and every client: NGO compares a hash of the config (tick rate, approval,
    /// scene management, the prefab list) on connect, so every side must come from here. Holds the
    /// references to <c>Prefabs/Player.prefab</c>, <c>Prefabs/Beacon.prefab</c> and
    /// <c>Prefabs/ScoreBoard.prefab</c>, which keeps them in the build and registers them in the same order
    /// on every side. Scene management is off: clients never load the game server's scene. A scene builder
    /// sets the three fields through <c>BeaconRushAssetBuilder.AssignNetworkPrefabs</c>.
    /// </summary>
    public sealed class BeaconRushNetwork : MonoBehaviour
    {
        public const string PlayerPrefabField = "playerPrefab";
        public const string BeaconPrefabField = "beaconPrefab";
        public const string ScoreBoardPrefabField = "scoreBoardPrefab";

        [SerializeField]
        [Tooltip("Prefabs/Player.prefab; spawned by NGO for every approved connection.")]
        private GameObject playerPrefab;

        [SerializeField]
        [Tooltip("Prefabs/Beacon.prefab; spawned by the game server during a match.")]
        private GameObject beaconPrefab;

        [SerializeField]
        [Tooltip("Prefabs/ScoreBoard.prefab; spawned once by the game server when it listens.")]
        private GameObject scoreBoardPrefab;

        public GameObject PlayerPrefab => playerPrefab;

        public GameObject BeaconPrefab => beaconPrefab;

        public GameObject ScoreBoardPrefab => scoreBoardPrefab;

        /// <summary>A game server (or listen host) bound to <c>0.0.0.0:&lt;port&gt;</c> over UDP. Call <see cref="NetworkManager.StartServer"/> or <see cref="NetworkManager.StartHost"/> next.</summary>
        public NetworkManager CreateServer(ushort port) => Create("0.0.0.0", port, "0.0.0.0");

        /// <summary>A client of the game server at <paramref name="address"/>:<paramref name="port"/>, sending <paramref name="connectionData"/> (an encoded join ticket).</summary>
        public NetworkManager CreateClient(string address, ushort port, byte[] connectionData)
        {
            NetworkManager manager = Create(address, port, null);
            manager.NetworkConfig.ConnectionData = connectionData ?? Array.Empty<byte>();
            return manager;
        }

        private NetworkManager Create(string address, ushort port, string listenAddress)
        {
            if (playerPrefab == null || beaconPrefab == null || scoreBoardPrefab == null)
            {
                throw new InvalidOperationException("BeaconRushNetwork is missing a prefab; regenerate the scenes with BeaconRushAssetBuilder.");
            }

            // NGO refuses a nested NetworkManager, so it lives on a root object of its own.
            var root = new GameObject("NetworkManager");
            UnityTransport transport = root.AddComponent<UnityTransport>();
            NetworkManager manager = root.AddComponent<NetworkManager>();
            if (manager.NetworkConfig == null)
            {
                manager.NetworkConfig = new NetworkConfig();
            }

            NetworkConfig config = manager.NetworkConfig;
            config.NetworkTransport = transport;
            config.PlayerPrefab = playerPrefab;
            config.TickRate = BeaconRushProtocol.TickRate;
            config.ConnectionApproval = true;
            config.ClientConnectionBufferTimeout = BeaconRushProtocol.ClientConnectionBufferTimeout;
            config.EnableSceneManagement = false;
            config.Prefabs.Add(new NetworkPrefab { Prefab = beaconPrefab });
            config.Prefabs.Add(new NetworkPrefab { Prefab = scoreBoardPrefab });

            // forceOverrideCommandLineArgs: the game parses -port itself, so UnityTransport must not re-read it.
            transport.SetConnectionData(true, address, port, listenAddress);
            return manager;
        }
    }
}
