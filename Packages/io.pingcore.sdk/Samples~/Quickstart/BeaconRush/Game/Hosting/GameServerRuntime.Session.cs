using System;
using System.Collections.Generic;
using BeaconRush.Match;
using BeaconRush.Session;
using UnityEngine;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The session side of <see cref="GameServerRuntime"/>: opening sessions (allocations, or local ones), carrying out
    /// the director's commands, and one <c>phase</c> event per phase with the match following it.
    /// </summary>
    public sealed partial class GameServerRuntime
    {
        private long lastPhaseVersion;

        /// <summary>
        /// Opens a session for <paramref name="allocationId"/> that waits for <paramref name="rosterPlayers"/> players. A
        /// different open session is replaced (its players stay connected but are no longer the session's).
        /// </summary>
        internal void OpenSession(string allocationId, SessionSettings settings, int rosterPlayers, bool local)
        {
            if (string.Equals(director.AllocationId, allocationId, StringComparison.Ordinal))
            {
                return;
            }

            if (director.HasSession)
            {
                CloseSessionSide();
            }

            AfterInput(director.OnAllocationReceived(allocationId, settings, rosterPlayers), local);
        }

        /// <summary>The platform cleared <paramref name="allocationId"/> without the game ending it.</summary>
        internal void CloseClearedSession(string allocationId)
        {
            if (!string.Equals(director.AllocationId, allocationId, StringComparison.Ordinal))
            {
                return;
            }

            director.OnAllocationCleared();
            lastPhaseVersion = director.PhaseVersion;
            DisconnectAll("session_cleared");
            CloseSessionSide();
        }

        private void OpenLocalSession()
        {
            string id = "local-" + ProcessIdentity.NewProcessId().Substring(0, 8);
            OpenSession(id, LocalSettings, 0, true);

            // Players stay connected between local sessions: the next one starts with everyone still here.
            foreach (ulong clientId in new List<ulong>(connectedClients))
            {
                AfterInput(director.OnPlayerJoined(clientId));
                match?.AddPlayer(clientId, NameOf(clientId));
            }
        }

        /// <summary>The settings of a local session: the default match, an unbounded lobby, and a listen host's own overrides.</summary>
        internal SessionSettings LocalSettings { get; set; } = SessionSettings.Local;

        /// <summary>Raises the phase the director entered, if it changed, then carries out <paramref name="command"/>.</summary>
        private void AfterInput(SessionCommand command, bool localOpen = false)
        {
            if (director.PhaseVersion != lastPhaseVersion)
            {
                lastPhaseVersion = director.PhaseVersion;
                if (director.HasSession)
                {
                    OnPhaseEntered(localOpen);
                }
            }

            if (!command.IsNone)
            {
                OnSessionEnded(command);
            }
        }

        private void OnPhaseEntered(bool localOpen)
        {
            SessionPhase phase = director.Phase;
            string cause = localOpen && phase == SessionPhase.Lobby ? "local" : SessionEndReasons.ToWire(director.Cause);
            ServerEvents.Raise(ServerEvents.Phase, "allocationId", director.AllocationId, "phase", SessionEndReasons.ToWire(phase),
                "players", director.Players, "expectedPlayers", director.ExpectedPlayers, "cause", cause);
            if (match != null)
            {
                match.OnPhase(true, phase, director.TimeLeft);
                if (phase == SessionPhase.Results)
                {
                    MatchOutcome outcome = match.Finish();
                    ServerEvents.Raise(ServerEvents.MatchResult, "allocationId", director.AllocationId,
                        "cause", SessionEndReasons.ToWire(director.Cause),
                        "winner", outcome.Winner.HasValue ? (object)(long)outcome.Winner.Value : null,
                        "topScore", outcome.TopScore, "players", director.Players, "pickups", match.Pickups);
                }
            }

            mode?.OnPhase(director.AllocationId, phase, director.Settings, director.Players);
        }

        private void OnSessionEnded(SessionCommand command)
        {
            CloseSessionSide();
            if (mode == null)
            {
                return;
            }

            if (mode.UsesAllocations)
            {
                DisconnectAll("session_ended");
                CarryEnd(command);
                return;
            }

            ServerEvents.Raise(ServerEvents.SessionEnded, "allocationId", command.AllocationId, "reason", SessionEndReasons.ToWire(command.Reason),
                "status", 0, "outcome", "local");
            if (!stopping)
            {
                OpenLocalSession();
            }
        }

        private async void CarryEnd(SessionCommand command)
        {
            try
            {
                await mode.EndSessionAsync(command, lifetime == null ? default : lifetime.Token);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>The match and the mode forget the session that just closed.</summary>
        private void CloseSessionSide()
        {
            match?.OnSessionClosed();
            mode?.OnSessionClosed();
        }
    }
}
