using System.Collections.Generic;
using BeaconRush.Client.Flows;
using BeaconRush.Client.Models;
using BeaconRush.Match;
using BeaconRush.Session;
using PingCore.Core.Handshake;
using Unity.Netcode;

namespace BeaconRush.Client.UI
{
    /// <summary>The connection and the session: Connecting, Lobby and Match (the HUD), Results, Leave and Play again.</summary>
    public sealed partial class ClientUi
    {
        private string sessionWinner;

        /// <summary>True when the session on screen is this player's own listen host (no client connection).</summary>
        internal bool IsOwnHostSession => connection == null && listenHost != null && listenHost.IsRunning;

        /// <summary>The last result the game server showed in this session, for example <c>Red wins: Alice (10)</c>, or null.</summary>
        internal string SessionWinner => sessionWinner;

        /// <summary>The game server's score board while it is spawned, else null.</summary>
        internal static ScoreBoard Board
        {
            get
            {
                ScoreBoard board = ScoreBoard.Instance;
                return board != null && board.IsSpawned ? board : null;
            }
        }

        /// <summary>Gives up on a connection that has not been answered yet.</summary>
        internal void CancelConnecting()
        {
            GameConnection pending = connection;
            connection = null;
            pending?.Dispose();
            Progress("Joining cancelled.");
            screen = returnScreen;
        }

        /// <summary>Leaves the session (a client connection), or goes back to the Host screen from this player's own host.</summary>
        internal void LeaveSession()
        {
            if (IsOwnHostSession)
            {
                screen = ClientScreen.Host;
                return;
            }

            GameConnection leaving = connection;
            connection = null;
            lobby.Leave("left");
            leaving?.Dispose();
            screen = ClientScreen.Results;
        }

        /// <summary>From Results back to the main menu, ready for the next game.</summary>
        internal void PlayAgain()
        {
            matchmaking.Reset();
            connection?.Dispose();
            connection = null;
            ClearNotice();
            screen = ClientScreen.Home;
        }

        /// <summary>
        /// Connects to a game server with a join ticket; the HUD on approval, back with the reason in plain words on a refusal,
        /// or <paramref name="onRefused"/> instead when given (Quick play's retry).
        /// </summary>
        private void Connect(GameEndpoint endpoint, JoinTicket ticket, int expectedPlayers, System.Action<string> onRefused = null)
        {
            connection?.Dispose();
            if (!JoinTicketCodec.TryEncode(ticket, out byte[] payload, out string problem))
            {
                Fail("The join ticket could not be written: " + problem);
                return;
            }

            connection = new GameConnection(boot.Network);
            connection.Approved += c =>
            {
                ClearNotice();
                sessionWinner = null;
                lobby.Join(Now, expectedPlayers, LobbyTimeout);
                screen = ClientScreen.Lobby;
            };
            connection.Rejected += c =>
            {
                if (onRefused != null)
                {
                    onRefused(c.Reason);
                    return;
                }

                Fail(ConnectionText.Refused(c.Reason));
                screen = returnScreen;
            };
            connection.Left += c =>
            {
                lobby.Leave(c.Reason);
                screen = ClientScreen.Results;
            };
            ClearNotice();
            ConnectingText = "Joining the game server at " + endpoint + " (" + JoinTicketKinds.ToWire(ticket.Kind) + " join)...";
            screen = ClientScreen.Connecting;
            connection.Start(endpoint, payload);
        }

        /// <summary>Joins this player's own listen host as its local player.</summary>
        private void PlayOwnSession()
        {
            sessionWinner = null;
            lobby.Join(Now, 1, LobbyTimeout);
            screen = ClientScreen.Lobby;
        }

        /// <summary>Every frame in a session: the roster and the phase the game server replicates, and the winner once it shows one.</summary>
        private void TrackSession()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || (screen != ClientScreen.Lobby && screen != ClientScreen.Match))
            {
                return;
            }

            ScoreBoard board = Board;
            lobby.SetRoster(ReadRoster(manager, board));
            if (board != null)
            {
                lobby.SetReportedPhase(ToView(board.Phase), board.SecondsLeft);
                if (board.Phase == SessionPhase.Results)
                {
                    sessionWinner = WinnerText(board.Winner, lobby.Roster);
                }
            }

            screen = lobby.Phase(Now) == SessionView.Lobby ? ClientScreen.Lobby : ClientScreen.Match;
        }

        /// <summary>The winner line: <c>Red wins: Alice (10)</c>, or <c>A draw</c>. Colours by join order, as the arena draws them.</summary>
        internal static string WinnerText(ulong? winner, IReadOnlyList<LobbyEntry> roster)
        {
            if (!winner.HasValue)
            {
                return "A draw";
            }

            foreach (LobbyEntry entry in roster)
            {
                if (entry.ClientId == winner.Value)
                {
                    string colour = PlayerPalette.Name(PlayerPalette.SlotFor(entry.ClientId, Ids(roster)));
                    return (entry.IsLocal ? "You win! " : colour + " wins: ") + entry.Name + " (" + entry.Score + ")";
                }
            }

            return "Player " + winner.Value + " wins";
        }

        /// <summary>The client ids of a roster, for <see cref="PlayerPalette.SlotFor"/>.</summary>
        internal static List<ulong> Ids(IReadOnlyList<LobbyEntry> roster)
        {
            var ids = new List<ulong>(roster.Count);
            foreach (LobbyEntry entry in roster)
            {
                ids.Add(entry.ClientId);
            }

            return ids;
        }

        /// <summary>The game server's score board when it has spawned (names and scores), else the spawned player objects.</summary>
        private IReadOnlyList<LobbyEntry> ReadRoster(NetworkManager manager, ScoreBoard board)
        {
            var entries = new List<LobbyEntry>();
            if (board != null)
            {
                foreach (ScoreEntry entry in board.Entries)
                {
                    entries.Add(new LobbyEntry(entry.ClientId, entry.Name.ToString(), entry.Score, entry.ClientId == manager.LocalClientId));
                }

                return entries;
            }

            if (manager.SpawnManager == null)
            {
                return entries;
            }

            foreach (NetworkObject spawned in manager.SpawnManager.SpawnedObjectsList)
            {
                if (spawned != null && spawned.IsPlayerObject)
                {
                    bool local = spawned.OwnerClientId == manager.LocalClientId;
                    entries.Add(new LobbyEntry(spawned.OwnerClientId, local ? displayName : null, 0, local));
                }
            }

            entries.Sort((a, b) => a.ClientId.CompareTo(b.ClientId));
            return entries;
        }

        private static SessionView ToView(SessionPhase phase)
        {
            switch (phase)
            {
                case SessionPhase.Match: return SessionView.Match;
                case SessionPhase.Results: return SessionView.Results;
                default: return SessionView.Lobby;
            }
        }
    }
}
