using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI.Views
{
    /// <summary>
    /// Every view of the client on one <see cref="UIDocument"/> (<c>Layout/ClientMenu.uxml</c>): one element per screen, of
    /// which <see cref="Show"/> displays exactly one, plus the status line under them. Each frame <see cref="Refresh"/>
    /// updates the visible view from the <see cref="ClientUi"/> state; the views only read state and call the
    /// <see cref="ClientUi"/> actions.
    /// </summary>
    internal sealed class ClientViews
    {
        private readonly VisualElement root;
        private readonly ClientUi ui;
        private readonly Dictionary<ClientScreen, VisualElement> screens = new Dictionary<ClientScreen, VisualElement>();
        private readonly Dictionary<ClientScreen, Action<DateTimeOffset>> refreshers = new Dictionary<ClientScreen, Action<DateTimeOffset>>();
        private readonly VisualElement statusBar;
        private readonly Label statusText;
        private VisualElement shown;
        private ClientScreen? current;

        public ClientViews(VisualElement root, ClientUi ui)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.ui = ui;

            var menu = new MenuView(root, ui);
            var find = new FindMatchView(root, ui);
            var browse = new BrowseView(root, ui);
            var reservation = new ReservationView(root, ui);
            var host = new HostView(root, ui);
            var direct = new DirectConnectView(root, ui);
            var hud = new HudView(root, ui);
            var results = new ResultsView(root, ui);

            Add(ClientScreen.Home, "screen-menu", menu.Refresh);
            Add(ClientScreen.FindMatch, "screen-find", find.Refresh);
            Add(ClientScreen.Browse, "screen-browse", browse.Refresh);
            Add(ClientScreen.JoinReservation, "screen-reservation", reservation.Refresh);
            Add(ClientScreen.Host, "screen-host", host.Refresh);
            Add(ClientScreen.DirectConnect, "screen-direct", direct.Refresh);
            Add(ClientScreen.Lobby, "hud", hud.Refresh);
            Add(ClientScreen.Match, "hud", hud.Refresh);
            Add(ClientScreen.Results, "screen-results", results.Refresh);

            Label connecting = Ui.Require<Label>(root, "connecting-text");
            Ui.Require<Button>(root, "connecting-cancel").clicked += ui.CancelConnecting;
            Add(ClientScreen.Connecting, "screen-connecting", now => Ui.Text(connecting, ui.ConnectingText));

            statusBar = Ui.Require<VisualElement>(root, "status-bar");
            statusText = Ui.Require<Label>(root, "status-text");
            Ui.Require<Button>(root, "status-dismiss").clicked += ui.ClearNotice;
        }

        /// <summary>Displays the element of <paramref name="screen"/> and hides the rest. Focus leaves a hidden view.</summary>
        public void Show(ClientScreen screen)
        {
            if (current == screen)
            {
                return;
            }

            current = screen;
            VisualElement next = screens[screen];
            if (next == shown)
            {
                return;
            }

            if (shown != null)
            {
                Ui.Show(shown, false);
            }

            root.focusController?.focusedElement?.Blur();
            Ui.Show(next, true);
            shown = next;
        }

        /// <summary>Updates the visible view and the status line.</summary>
        public void Refresh(DateTimeOffset now)
        {
            if (current.HasValue)
            {
                refreshers[current.Value](now);
            }

            bool hasNotice = !string.IsNullOrEmpty(ui.Notice);
            Ui.Show(statusBar, hasNotice);
            statusBar.EnableInClassList("br-error", ui.NoticeIsError);
            if (hasNotice)
            {
                Ui.Text(statusText, ui.Notice);
            }
        }

        private void Add(ClientScreen screen, string elementName, Action<DateTimeOffset> refresh)
        {
            VisualElement element = Ui.Require<VisualElement>(root, elementName);
            Ui.Show(element, false);
            screens[screen] = element;
            refreshers[screen] = refresh;
        }
    }
}
