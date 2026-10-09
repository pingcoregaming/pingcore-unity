using System;
using System.Threading.Tasks;
using BeaconRush.Client.Models;
using BeaconRush.Networking;
using PingCore.Core.Discovery;
using PingCore.Core.Handshake;
using PingCore.Discovery.Client;
using PingCore.Discovery.Client.Wire;
using UnityEngine;

namespace BeaconRush.Client.UI
{
    /// <summary>The flows behind Browse (Fleet and Community tabs) and Join by game server id.</summary>
    public sealed partial class ClientUi
    {
        private bool latencyStarted;

        /// <summary>True while a reservation is being made.</summary>
        internal bool Reserving { get; private set; }

        /// <summary>The app Join by game server id reserves on.</summary>
        internal ClientApp ReservationApp { get; set; } = ClientApp.Fleet;

        /// <summary>The id typed on Join by game server id.</summary>
        internal string ReservationServerId { get; set; } = string.Empty;

        /// <summary>Asks a browser tab for its current page, when its refresh limit allows.</summary>
        internal void RefreshBrowse(ClientApp app) => _ = RefreshAsync(app);

        /// <summary>Reserves a seat on a listed game server and joins it.</summary>
        internal void JoinListed(ClientApp app, BrowserRow row)
        {
            ReservationApp = app;
            ReservationServerId = row.ServerId;
            returnScreen = ClientScreen.Browse;
            _ = ReserveAndJoinAsync(app, row.ServerId);
        }

        /// <summary>Reserves a seat on the game server typed on Join by game server id and joins it.</summary>
        internal void JoinById()
        {
            if (string.IsNullOrWhiteSpace(ReservationServerId))
            {
                return;
            }

            returnScreen = ClientScreen.JoinReservation;
            _ = ReserveAndJoinAsync(ReservationApp, ReservationServerId.Trim());
        }

        private void BeginLatency()
        {
            if (latencyStarted || !services.IsConfigured(ClientApp.Fleet) || !LatencyProbe.IsSupported)
            {
                return;
            }

            latencyStarted = true;
            _ = MeasureLatencyAsync();
        }

        private async Task MeasureLatencyAsync()
        {
            try
            {
                LatencyResult result = await services.For(ClientApp.Fleet).MeasureLatencyAsync(new LatencyProbeOptions(), lifetime.Token);
                if (result.IsOk)
                {
                    browser.SetLatency(result.Medians);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ClientUi] latency measurement failed: " + e.GetType().Name);
            }
        }

        private async Task RefreshAsync(ClientApp app)
        {
            if (!browser.TryBeginRefresh(app, Now, out ServerListQuery query))
            {
                return;
            }

            try
            {
                DiscoveryResult<ServerPage> page = await services.For(app).ListServersAsync(query, lifetime.Token);
                if (page.IsOk)
                {
                    browser.ApplyPage(app, page.Value);
                }
                else
                {
                    browser.ApplyFailure(app, Describe(page));
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                browser.ApplyFailure(app, e.GetType().Name);
            }
        }

        private async Task ReserveAndJoinAsync(ClientApp app, string serverId)
        {
            if (Reserving)
            {
                return;
            }

            Reserving = true;
            Progress("Reserving a seat...");
            try
            {
                DiscoveryClient client = services.For(app);
                DiscoveryResult<ReservationResponse> held = await client.ReserveAsync(serverId, new ReserveOptions(), lifetime.Token);
                if (!held.IsOk || held.Value == null)
                {
                    string refused = "Could not reserve a seat: " + Describe(held) + (held.Error != null && held.Error.Available.HasValue ? " (" + held.Error.Available + " seats free)" : string.Empty);
                    // The check reads the fleet app; a community or self-host seat has its own answer.
                    Fail(app == ClientApp.Fleet ? await JoinFailureTextAsync(refused) : refused);
                    return;
                }

                ReservationResponse hold = held.Value;
                if (string.IsNullOrEmpty(hold.Ip) || !hold.Port.HasValue || hold.Port.Value < 1 || hold.Port.Value > 65535)
                {
                    Fail("The reservation came back without an address to connect to.");
                    return;
                }

                string playerId = client.Tokens.Current != null ? client.Tokens.Current.PlayerId : null;
                if (string.IsNullOrEmpty(playerId))
                {
                    Fail("Discovery gave this player no id, so the seat cannot be used.");
                    return;
                }

                if (!GameEndpoint.TryParse(hold.Ip + ":" + hold.Port.Value, out GameEndpoint endpoint, out string problem))
                {
                    Fail("The reservation's address is not usable: " + problem);
                    return;
                }

                Connect(endpoint, JoinTicket.ForReservation(hold.ReservationId, playerId, BeaconRushProtocol.Version, JoinName), BeaconRushProtocol.MaxPlayers);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Fail("Could not reserve a seat: " + e.GetType().Name);
            }
            finally
            {
                Reserving = false;
            }
        }

        /// <summary>A Discovery answer in words, for example <c>RateLimited (429): rate_limited, retry in 5 s</c>.</summary>
        internal static string Describe(DiscoveryCallResult result)
        {
            if (result == null)
            {
                return "no answer";
            }

            string reason = string.IsNullOrEmpty(result.ReasonWire) ? null : result.ReasonWire;
            return result.Outcome + (result.Status > 0 ? " (" + result.Status + ")" : string.Empty) + (reason != null ? ": " + reason : string.Empty)
                + (result.RetryAfter.HasValue ? ", retry in " + Math.Ceiling(result.RetryAfter.Value.TotalSeconds) + " s" : string.Empty);
        }
    }
}
