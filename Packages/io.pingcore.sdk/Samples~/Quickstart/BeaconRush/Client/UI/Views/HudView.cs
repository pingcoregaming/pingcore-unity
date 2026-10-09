using System;
using BeaconRush.Client.Models;
using BeaconRush.Match;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI.Views
{
    /// <summary>
    /// The HUD over the arena during a session, from the game server's <see cref="ScoreBoard"/> through the
    /// <see cref="LobbyModel"/>: the score list (colour by join order, this player marked), the phase with the goal, the timer,
    /// a "match over" card with the winner while the game server shows results, and Leave (or Back to hosting on this
    /// player's own listen host).
    /// </summary>
    internal sealed class HudView
    {
        private readonly ClientUi ui;
        private readonly VisualElement scores;
        private readonly Label phase;
        private readonly Label timer;
        private readonly Button leave;
        private readonly VisualElement resultsCard;
        private readonly Label winner;
        private readonly Label nextLine;
        private readonly Label help;

        public HudView(VisualElement root, ClientUi ui)
        {
            this.ui = ui;
            scores = Ui.Require<VisualElement>(root, "hud-scores");
            phase = Ui.Require<Label>(root, "hud-phase");
            timer = Ui.Require<Label>(root, "hud-timer");
            leave = Ui.Require<Button>(root, "hud-leave");
            resultsCard = Ui.Require<VisualElement>(root, "hud-results");
            winner = Ui.Require<Label>(root, "hud-winner");
            nextLine = Ui.Require<Label>(root, "hud-next");
            help = Ui.Require<Label>(root, "hud-help");
            leave.clicked += ui.LeaveSession;
        }

        public void Refresh(DateTimeOffset now)
        {
            LobbyModel lobby = ui.Lobby;
            SessionView view = lobby.Phase(now);
            Ui.ScoreRows(scores, lobby.Roster);
            Ui.Text(leave, ui.IsOwnHostSession ? "Back to hosting" : "Leave");
            TimeSpan left = lobby.TimerRemaining(now);
            Ui.Text(timer, left > TimeSpan.Zero ? MatchmakingModel.FormatClock(left) : view == SessionView.Lobby ? "--:--" : "0:00");
            switch (view)
            {
                case SessionView.Lobby:
                    Ui.Text(phase, "LOBBY  ·  " + lobby.LobbyText(now).ToUpperInvariant());
                    break;
                case SessionView.Match:
                    Ui.Text(phase, "MATCH  ·  FIRST TO " + MatchRules.ScoreLimit + " WINS");
                    break;
                default:
                    Ui.Text(phase, "RESULTS");
                    break;
            }

            bool results = view == SessionView.Results;
            Ui.Show(resultsCard, results);
            Ui.Show(help, view == SessionView.Match);
            if (results)
            {
                Ui.Text(winner, ui.SessionWinner ?? "Match over");
                Ui.Text(nextLine, left > TimeSpan.Zero ? "The game server closes the match in " + MatchmakingModel.FormatClock(left) + "." : string.Empty);
            }
        }
    }

    /// <summary>Results after the session ended: the winner, why it ended in plain words, the final scores and Play again.</summary>
    internal sealed class ResultsView
    {
        private readonly ClientUi ui;
        private readonly Label title;
        private readonly Label winner;
        private readonly Label reason;
        private readonly VisualElement scores;

        public ResultsView(VisualElement root, ClientUi ui)
        {
            this.ui = ui;
            title = Ui.Require<Label>(root, "results-title");
            winner = Ui.Require<Label>(root, "results-winner");
            reason = Ui.Require<Label>(root, "results-reason");
            scores = Ui.Require<VisualElement>(root, "results-scores");
            Ui.Require<Button>(root, "results-again").clicked += ui.PlayAgain;
        }

        public void Refresh(DateTimeOffset now)
        {
            LobbyModel lobby = ui.Lobby;
            bool decided = ui.SessionWinner != null;
            Ui.Text(title, decided ? "Match over" : "Session ended");
            Ui.Show(winner, decided);
            Ui.Text(winner, ui.SessionWinner);
            Ui.Text(reason, ConnectionText.Describe(lobby.EndReason));
            Ui.ScoreRows(scores, lobby.FinalScores);
        }
    }
}
