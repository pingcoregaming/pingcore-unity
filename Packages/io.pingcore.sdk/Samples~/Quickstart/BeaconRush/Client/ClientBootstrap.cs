using BeaconRush.Client.Flows;
using BeaconRush.Client.Models;
using BeaconRush.Networking;
using PingCore.Discovery.Client;
using UnityEngine;

namespace BeaconRush.Client
{
    /// <summary>
    /// The root of <c>Scenes/Client.unity</c>: it holds the client settings asset (the Discovery URL and the
    /// public <c>dscp_</c> ids; the heartbeat token there stays empty), the self-host app's public id, and the
    /// <see cref="BeaconRushNetwork"/> every connection and every player-hosted game server is built from.
    /// Interactive players get <c>ClientUi</c>; a headless process (<see cref="ClientLaunch.Headless"/>) or a
    /// batchmode run gets no UI at all.
    /// </summary>
    public sealed class ClientBootstrap : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Settings/PingCoreClientSettings.asset: Discovery URL and the fleet and community app ids.")]
        private PingCoreClientSettings settings;

        [SerializeField]
        [Tooltip("Public id (dscp_...) of the private self-host Discovery app; the placeholder until provisioning creates it.")]
        private string selfHostAppPublicId = ClientApps.PlaceholderPublicId;

        [SerializeField]
        [Tooltip("The NGO setup shared with the game server.")]
        private BeaconRushNetwork network;

        /// <summary>The bootstrap of the loaded client scene, or null.</summary>
        public static ClientBootstrap Instance { get; private set; }

        /// <summary>The client settings asset.</summary>
        public PingCoreClientSettings Settings => settings;

        /// <summary>The self-host app's public id.</summary>
        public string SelfHostAppPublicId => selfHostAppPublicId;

        /// <summary>The shared NGO setup.</summary>
        public BeaconRushNetwork Network => network;

        /// <summary>The Discovery clients for this player, one per app.</summary>
        public ClientServices CreateServices(ClientServicesOptions options)
        {
            return new ClientServices(settings.DiscoveryBaseUrl, settings.FleetAppPublicId, settings.CommunityAppPublicId, selfHostAppPublicId, options);
        }

        private void Awake()
        {
            Instance = this;
            Application.targetFrameRate = ClientLaunch.Headless || Application.isBatchMode ? BeaconRushProtocol.TickRate : 60;
            Application.runInBackground = true;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
