using System;
using System.Threading.Tasks;
using BeaconRush.Client.Models;
using BeaconRush.Hosting;
using BeaconRush.Networking;
using PingCore.Core.Handshake;
using PingCore.Discovery.Host;
using UnityEngine;

namespace BeaconRush.Client.UI
{
    /// <summary>
    /// The flows behind Host a game (Online or LAN only, through the game's <see cref="ListenHost"/>) and Direct connect. The
    /// heartbeat token is read by <see cref="ListenHost"/> from <c>PINGCORE_DISCOVERY_TOKEN</c>; the client only learns whether it is set.
    /// </summary>
    public sealed partial class ClientUi
    {
        /// <summary>The address typed on Direct connect.</summary>
        internal string DirectText { get; set; } = "127.0.0.1:" + HostModel.DefaultPort;

        /// <summary>Starts the listen host on the typed port.</summary>
        internal void StartHosting() => _ = StartHostingAsync();

        /// <summary>Stops the listen host.</summary>
        internal void StopHosting() => _ = StopHostingAsync();

        /// <summary>Plays on this player's own listen host.</summary>
        internal void PlayHosted() => PlayOwnSession();

        /// <summary>Joins a LAN-only host at the typed address with a <c>lan</c> join ticket.</summary>
        internal void DirectConnect()
        {
            if (!GameEndpoint.TryParse(DirectText, out GameEndpoint endpoint, out string problem))
            {
                Fail("That address is not usable: " + problem);
                return;
            }

            returnScreen = ClientScreen.DirectConnect;
            Connect(endpoint, JoinTicket.ForLan(lanPlayerId, BeaconRushProtocol.Version, JoinName), BeaconRushProtocol.MaxPlayers);
        }

        /// <summary>The host's status line, refreshed with the heartbeat's state while hosting Online.</summary>
        internal string HostStatusText()
        {
            if (host.Status == HostStatus.Hosting && host.Reach == HostReach.Online)
            {
                HeartbeatStatus status = listenHost.HeartbeatStatus;
                host.Hosting("Listed as " + (listenHost.ServerId ?? "(not yet)") + (status?.Verified != null ? ", reachability " + status.Verified : string.Empty)
                    + (string.IsNullOrEmpty(status?.LastProbeError) ? string.Empty : " (" + status.LastProbeError + ")") + ".");
            }

            return host.StatusText();
        }

        private async Task StartHostingAsync()
        {
            if (!host.TryGetPort(out int port, out string problem))
            {
                Fail("Cannot host: " + problem + ".");
                return;
            }

            bool online = host.Reach == HostReach.Online;
            ClearNotice();
            host.Starting(port);
            try
            {
                ListenHostStartResult started = await listenHost.StartAsync(new ListenHostOptions
                {
                    Port = (ushort)port,
                    LanOnly = !online,
                    DiscoveryUrl = services.DiscoveryBaseUrl,
                    ListingName = (JoinName ?? "Player") + " Beacon Rush",
                    HostDisplayName = JoinName,
                }, lifetime.Token);
                switch (started.Outcome)
                {
                    case ListenHostStartOutcome.Started:
                        host.Hosting(online ? HeartbeatNote(started.Heartbeat) : null);
                        break;
                    case ListenHostStartOutcome.NoDiscoveryToken:
                        host.Fail(HostModel.NoTokenHint);
                        break;
                    case ListenHostStartOutcome.ListenFailed:
                        host.Fail("port " + port + " is in use");
                        break;
                    default:
                        host.Fail("already hosting");
                        break;
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                // Never show an exception message here: a refused token option could be quoted in one.
                host.Fail(e.GetType().Name);
            }
        }

        private async Task StopHostingAsync()
        {
            try
            {
                await listenHost.StopAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ClientUi] stopping the host failed: " + e.GetType().Name);
            }

            host.Stopped();
            screen = ClientScreen.Host;
        }

        private static string HeartbeatNote(HeartbeatStartOutcome? outcome)
        {
            switch (outcome)
            {
                case HeartbeatStartOutcome.Started: return "Discovery accepted the heartbeat.";
                case HeartbeatStartOutcome.Refused: return "Discovery refused the heartbeat (a per-address or self-hosted cap); the host is not listed.";
                case HeartbeatStartOutcome.Failed: return "Discovery did not answer yet; retrying.";
                default: return null;
            }
        }
    }
}
