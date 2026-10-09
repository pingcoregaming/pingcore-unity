using System;
using System.Collections.Generic;
using BeaconRush.Client.Models;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client;
using PingCore.Unity;

namespace BeaconRush.Client.Flows
{
    /// <summary>How <see cref="ClientServices"/> creates its Discovery clients.</summary>
    public sealed class ClientServicesOptions
    {
        /// <summary>Where anonymous player tokens are kept; null for <c>PlayerPrefsTokenStore</c> (the interactive game).</summary>
        public IPlayerTokenStore TokenStore { get; set; }

        /// <summary>Separates player identities sharing one store (several clients on one PC); empty for none.</summary>
        public string Profile { get; set; } = string.Empty;

        /// <summary>A studio-signed player token for the fleet app, or null for anonymous issuance. Never logged.</summary>
        public string SignedFleetToken { get; set; }

        /// <summary>A public id that replaces the settings' id for <see cref="OverrideApp"/>, or null.</summary>
        public string AppPublicIdOverride { get; set; }

        /// <summary>The app <see cref="AppPublicIdOverride"/> applies to.</summary>
        public ClientApp OverrideApp { get; set; }

        /// <summary>The Discovery client's log sink, or null. Its entries carry no token, ticket id, header or body.</summary>
        public Action<DiscoveryLogEntry> Log { get; set; }
    }

    /// <summary>
    /// One <see cref="DiscoveryClient"/> per Discovery app (player tokens are per app), created on first use
    /// from the settings asset: the fleet app for matchmaking, quick join and the Fleet tab; the community
    /// app for the Community tab; the self-host app for self-hosted game servers behind NAT. Holds no
    /// backend credential: the Discovery client refuses anything shaped like one.
    /// </summary>
    public sealed class ClientServices : IDisposable
    {
        private readonly string baseUrl;
        private readonly Dictionary<ClientApp, string> publicIds = new Dictionary<ClientApp, string>();
        private readonly Dictionary<ClientApp, DiscoveryClient> clients = new Dictionary<ClientApp, DiscoveryClient>();
        private readonly ClientServicesOptions options;

        /// <summary>Creates the services from the Discovery URL and the three public ids.</summary>
        public ClientServices(string discoveryBaseUrl, string fleetAppPublicId, string communityAppPublicId, string selfHostAppPublicId, ClientServicesOptions options)
        {
            baseUrl = discoveryBaseUrl;
            this.options = options ?? new ClientServicesOptions();
            publicIds[ClientApp.Fleet] = fleetAppPublicId;
            publicIds[ClientApp.Community] = communityAppPublicId;
            publicIds[ClientApp.SelfHost] = selfHostAppPublicId;
            if (!string.IsNullOrEmpty(this.options.AppPublicIdOverride))
            {
                publicIds[this.options.OverrideApp] = this.options.AppPublicIdOverride;
            }
        }

        /// <summary>Raised for every player-token change of any app; the event never carries the token.</summary>
        public event Action<ClientApp, PlayerTokenEvent> TokenChanged;

        /// <summary>The Discovery base URL.</summary>
        public string DiscoveryBaseUrl => baseUrl;

        /// <summary>The app's public id (possibly the placeholder).</summary>
        public string PublicIdFor(ClientApp app) => publicIds.TryGetValue(app, out string id) ? id : null;

        /// <summary>True when the app has a real public id, not the shipped placeholder.</summary>
        public bool IsConfigured(ClientApp app) => !ClientApps.IsUnconfigured(PublicIdFor(app)) && DiscoveryClient.IsAppPublicId(PublicIdFor(app));

        /// <summary>The app's Discovery client, created on first use. Throws <see cref="InvalidOperationException"/> for an unconfigured app.</summary>
        public DiscoveryClient For(ClientApp app)
        {
            if (clients.TryGetValue(app, out DiscoveryClient existing))
            {
                return existing;
            }

            if (!IsConfigured(app))
            {
                throw new InvalidOperationException("the " + ClientApps.ToName(app) + " Discovery app is not configured (its public id is the placeholder)");
            }

            DiscoveryClient client = DiscoveryClient.Create(new DiscoveryClientOptions
            {
                BaseUrl = baseUrl,
                AppPublicId = PublicIdFor(app),
                TokenStore = options.TokenStore ?? new PlayerPrefsTokenStore(),
                Profile = options.Profile ?? string.Empty,
                Log = options.Log,
            });
            client.Tokens.Changed += change => Raise(app, change);
            if (app == ClientApp.Fleet && !string.IsNullOrEmpty(options.SignedFleetToken))
            {
                client.Tokens.SetSignedToken(options.SignedFleetToken);
            }

            clients[app] = client;
            return client;
        }

        /// <summary>Stops every poll loop of every client.</summary>
        public void Dispose()
        {
            foreach (DiscoveryClient client in clients.Values)
            {
                client.Dispose();
            }

            clients.Clear();
        }

        private void Raise(ClientApp app, PlayerTokenEvent change)
        {
            try
            {
                TokenChanged?.Invoke(app, change);
            }
            catch (Exception)
            {
                // A listener's failure must not break a call.
            }
        }
    }
}
