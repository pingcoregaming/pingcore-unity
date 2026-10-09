using System;
using System.Threading.Tasks;
using BeaconRush.Client.Models;
using BeaconRush.Networking;
using PingCore.Core.Discovery;
using PingCore.Core.Handshake;
using PingCore.Discovery.Client;
using PingCore.Discovery.Client.Wire;

namespace BeaconRush.Client.UI
{
    /// <summary>The flows behind Find match (the <c>rush-p&lt;protocol&gt;</c> queue) and Quick play (quick join).</summary>
    public sealed partial class ClientUi
    {
        private TicketHandle ticket;
        private int quickPlayAttempt;

        /// <summary>The Find match screen's "also join a match in progress" choice.</summary>
        internal bool JoinInProgress { get; set; } = true;

        /// <summary>True while Quick play is looking for a game server.</summary>
        internal bool QuickJoining { get; private set; }

        /// <summary>True while a ticket can be cancelled.</summary>
        internal bool HasTicket => ticket != null;

        /// <summary>Queues a matchmaking ticket and joins the match it lands in.</summary>
        internal void FindMatch()
        {
            returnScreen = ClientScreen.FindMatch;
            _ = FindMatchAsync();
        }

        /// <summary>Cancels the queued ticket.</summary>
        internal void CancelTicket() => _ = CancelTicketAsync();

        /// <summary>Leaves Find match, dropping a finished search.</summary>
        internal void LeaveFindMatch()
        {
            if (matchmaking.CanCancel)
            {
                return;
            }

            matchmaking.Reset();
            Back();
        }

        /// <summary>
        /// Quick play: holds a seat on the best fleet game server with room (an idle one included, which claims itself for
        /// this player's session) and joins it alone. No seat anywhere falls back to Find match; a game server taken between
        /// the hold and the join is retried once (<see cref="QuickPlayPlan"/>).
        /// </summary>
        internal void QuickPlay()
        {
            if (QuickJoining)
            {
                return;
            }

            returnScreen = ClientScreen.Home;
            quickPlayAttempt = 0;
            _ = QuickJoinAsync();
        }

        private async Task FindMatchAsync()
        {
            DisposeTicket();
            matchmaking.BeginSubmit(Now);
            try
            {
                DiscoveryClient client = services.For(ClientApp.Fleet);
                TicketOptions options = matchmaking.CreateTicketOptions(JoinInProgress);
                LatencyResult latency = LatencyProbe.IsSupported ? await client.MeasureLatencyAsync(new LatencyProbeOptions(), lifetime.Token) : null;
                if (latency != null && latency.IsOk && latency.Medians.Count > 0)
                {
                    options.Latency = latency.Medians;
                }

                DiscoveryResult<TicketHandle> submitted = await client.SubmitTicketAsync(options, lifetime.Token);
                if (!submitted.IsOk || submitted.Value == null)
                {
                    matchmaking.Refuse(await JoinFailureTextAsync(Describe(submitted)), Now);
                    return;
                }

                ticket = submitted.Value;
                matchmaking.Apply(ticket.State, ticket.TicketRef, ticket.Match, null, Now);
                ticket.Changed += OnTicketChanged;
                TicketHandle done = await ticket.WaitAsync(lifetime.Token);
                if (done.State == TicketState.Matched && done.Match != null)
                {
                    JoinMatch(done);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                matchmaking.Refuse(e.GetType().Name, Now);
            }
        }

        private void OnTicketChanged(TicketHandle changed)
        {
            string reason = changed.LastError != null ? Describe(changed.LastError) : null;
            matchmaking.Apply(changed.State, changed.TicketRef, changed.Match, reason, Now);
        }

        private void JoinMatch(TicketHandle matched)
        {
            MatchAssignment placed = matched.Match;
            if (!GameEndpoint.TryParse(placed.Ip + ":" + placed.Port, out GameEndpoint endpoint, out string problem))
            {
                matchmaking.Refuse("the match's address is not usable: " + problem, Now);
                return;
            }

            Connect(endpoint, matched.CreateJoinTicket(BeaconRushProtocol.Version, JoinName), MatchmakingModel.SessionSize);
        }

        private async Task CancelTicketAsync()
        {
            TicketHandle current = ticket;
            if (current == null)
            {
                return;
            }

            try
            {
                DiscoveryResult<CancelTicketResponse> cancelled = await current.CancelAsync(lifetime.Token);
                if (!cancelled.IsOk && current.State != TicketState.Matched)
                {
                    Fail("Could not cancel the search: " + Describe(cancelled));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task QuickJoinAsync()
        {
            if (QuickJoining)
            {
                return;
            }

            QuickJoining = true;
            quickPlayAttempt++;
            if (quickPlayAttempt == 1)
            {
                Progress("Looking for a game server with room...");
            }

            try
            {
                DiscoveryClient client = services.For(ClientApp.Fleet);
                var options = new QuickJoinOptions();
                LatencyResult latency = LatencyProbe.IsSupported ? await client.MeasureLatencyAsync(new LatencyProbeOptions(), lifetime.Token) : null;
                if (latency != null && latency.IsOk && latency.Medians.Count > 0)
                {
                    options.Latency = latency.Medians;
                }

                DiscoveryResult<ReservationResponse> held = await client.QuickJoinAsync(options, lifetime.Token);
                bool answered = held.IsOk && held.Value != null && !string.IsNullOrEmpty(held.Value.Ip) && held.Value.Port.HasValue;
                string playerId = client.Tokens.Current != null ? client.Tokens.Current.PlayerId : null;
                GameEndpoint endpoint = default;
                bool usable = answered && !string.IsNullOrEmpty(playerId)
                    && GameEndpoint.TryParse(held.Value.Ip + ":" + held.Value.Port.Value, out endpoint, out _);
                switch (QuickPlayPlan.AfterQuickJoin(usable, held.ReasonWire))
                {
                    case QuickPlayStep.Connect:
                        string reservationId = held.Value.ReservationId;
                        Connect(endpoint, JoinTicket.ForReservation(reservationId, playerId, BeaconRushProtocol.Version, JoinName), BeaconRushProtocol.MaxPlayers,
                            literal => OnQuickPlayRefused(literal, reservationId));
                        return;
                    case QuickPlayStep.FindMatchInstead:
                        // No seat anywhere: queue for a match, unless there is no fleet app or no Discovery to queue with.
                        InfrastructureReport backend = await CheckInfrastructureAsync();
                        if (InfrastructureBanner.BlocksMatchmaking(backend))
                        {
                            Fail(backend.ToString());
                            return;
                        }

                        screen = ClientScreen.FindMatch;
                        FindMatch();
                        Progress(QuickPlayPlan.FindMatchNote);
                        return;
                    default:
                        Fail(answered
                            ? "Quick play's answer could not be used; try again."
                            : await JoinFailureTextAsync("Quick play could not find a game server: " + Describe(held)));
                        return;
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Fail("Quick play failed: " + e.GetType().Name);
            }
            finally
            {
                QuickJoining = false;
            }
        }

        /// <summary>A quick-play join was refused: give the seat back, then try once more when the game server was just taken, else say why.</summary>
        private void OnQuickPlayRefused(string literal, string reservationId)
        {
            // The seat on a game server that refused the join is no use now, retry or not; give it back, best effort.
            _ = ReleaseQuietlyAsync(reservationId);
            if (QuickPlayPlan.AfterRefusal(literal, quickPlayAttempt) != QuickPlayStep.RetryQuickPlay)
            {
                Fail(ConnectionText.Refused(literal));
                screen = returnScreen;
                return;
            }

            Progress(QuickPlayPlan.RetryNote);
            screen = ClientScreen.Home;
            _ = QuickJoinAsync();
        }

        private async Task ReleaseQuietlyAsync(string reservationId)
        {
            try
            {
                await services.For(ClientApp.Fleet).ReleaseReservationAsync(reservationId, lifetime.Token);
            }
            catch (Exception)
            {
                // Best effort: the hold lapses on its own TTL.
            }
        }

        private void DisposeTicket()
        {
            if (ticket != null)
            {
                ticket.Changed -= OnTicketChanged;
                ticket.Dispose();
                ticket = null;
            }
        }
    }
}
