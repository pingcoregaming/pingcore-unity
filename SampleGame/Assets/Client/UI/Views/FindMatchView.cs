using System;
using BeaconRush.Client.Models;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI.Views
{
    /// <summary>Find match: the queue, the "join a match in progress" choice, the search status and its timer, Find and Cancel.</summary>
    internal sealed class FindMatchView
    {
        private readonly ClientUi ui;
        private readonly Label queue;
        private readonly Toggle inProgress;
        private readonly Label status;
        private readonly Label ticketRef;
        private readonly Button go;
        private readonly Button cancel;
        private readonly Button back;

        public FindMatchView(VisualElement root, ClientUi ui)
        {
            this.ui = ui;
            queue = Ui.Require<Label>(root, "find-queue");
            inProgress = Ui.Require<Toggle>(root, "find-inprogress");
            status = Ui.Require<Label>(root, "find-status");
            ticketRef = Ui.Require<Label>(root, "find-ticket");
            go = Ui.Require<Button>(root, "find-go");
            cancel = Ui.Require<Button>(root, "find-cancel");
            back = Ui.Require<Button>(root, "find-back");

            inProgress.SetValueWithoutNotify(ui.JoinInProgress);
            inProgress.RegisterValueChangedCallback(change => ui.JoinInProgress = change.newValue);
            go.clicked += ui.FindMatch;
            cancel.clicked += ui.CancelTicket;
            back.clicked += ui.LeaveFindMatch;
        }

        public void Refresh(DateTimeOffset now)
        {
            MatchmakingModel model = ui.Matchmaking;
            Ui.Text(queue, "Queue " + model.Queue + ": " + MatchmakingModel.SessionSize + " players, or " + MatchmakingModel.MinSessionSize
                + " after " + MatchmakingModel.RelaxAfterSeconds + " s in the queue.");
            Ui.Text(status, model.StatusText(now));
            Ui.Text(ticketRef, model.TicketRef != null ? "Ticket " + model.TicketRef : string.Empty);
            Ui.Enable(inProgress, model.CanSearch);
            Ui.Enable(go, model.CanSearch);
            Ui.Enable(cancel, model.CanCancel && ui.HasTicket);
            Ui.Enable(back, !model.CanCancel);
        }
    }
}
