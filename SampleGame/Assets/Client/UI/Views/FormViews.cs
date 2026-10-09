using System;
using BeaconRush.Client.Models;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI.Views
{
    /// <summary>Join by game server id: the app (Fleet or Community), the id, Reserve and join.</summary>
    internal sealed class ReservationView
    {
        private readonly ClientUi ui;
        private readonly Button fleet;
        private readonly Button community;
        private readonly TextField id;
        private readonly Button go;

        public ReservationView(VisualElement root, ClientUi ui)
        {
            this.ui = ui;
            fleet = Ui.Require<Button>(root, "reservation-fleet");
            community = Ui.Require<Button>(root, "reservation-community");
            id = Ui.Require<TextField>(root, "reservation-id");
            go = Ui.Require<Button>(root, "reservation-go");
            fleet.clicked += () => ui.ReservationApp = ClientApp.Fleet;
            community.clicked += () => ui.ReservationApp = ClientApp.Community;
            id.RegisterValueChangedCallback(change => ui.ReservationServerId = change.newValue);
            go.clicked += ui.JoinById;
            Ui.Require<Button>(root, "reservation-back").clicked += ui.Back;
        }

        public void Refresh(DateTimeOffset now)
        {
            fleet.EnableInClassList("br-selected", ui.ReservationApp == ClientApp.Fleet);
            community.EnableInClassList("br-selected", ui.ReservationApp == ClientApp.Community);
            Ui.Enable(fleet, ui.Services.IsConfigured(ClientApp.Fleet));
            Ui.Enable(community, ui.Services.IsConfigured(ClientApp.Community));
            if (id.value != ui.ReservationServerId && id.focusController?.focusedElement != id)
            {
                id.SetValueWithoutNotify(ui.ReservationServerId);
            }

            Ui.Text(go, ui.Reserving ? "Reserving..." : "Reserve and join");
            Ui.Enable(go, !ui.Reserving && !string.IsNullOrWhiteSpace(ui.ReservationServerId));
        }
    }

    /// <summary>Host a game: Online or LAN only, the game port, the status (with the ports to forward), Start, Play, Stop.</summary>
    internal sealed class HostView
    {
        private readonly ClientUi ui;
        private readonly Button online;
        private readonly Button lan;
        private readonly Label tokenHint;
        private readonly TextField port;
        private readonly Label status;
        private readonly Button start;
        private readonly Button play;
        private readonly Button stop;
        private readonly Button back;

        public HostView(VisualElement root, ClientUi ui)
        {
            this.ui = ui;
            online = Ui.Require<Button>(root, "host-online");
            lan = Ui.Require<Button>(root, "host-lan");
            tokenHint = Ui.Require<Label>(root, "host-token-hint");
            port = Ui.Require<TextField>(root, "host-port");
            status = Ui.Require<Label>(root, "host-status");
            start = Ui.Require<Button>(root, "host-start");
            play = Ui.Require<Button>(root, "host-play");
            stop = Ui.Require<Button>(root, "host-stop");
            back = Ui.Require<Button>(root, "host-back");

            online.clicked += () => ui.Host.SetReach(HostReach.Online);
            lan.clicked += () => ui.Host.SetReach(HostReach.LanOnly);
            port.SetValueWithoutNotify(ui.Host.PortText);
            port.RegisterValueChangedCallback(change => ui.Host.PortText = change.newValue);
            start.clicked += ui.StartHosting;
            play.clicked += ui.PlayHosted;
            stop.clicked += ui.StopHosting;
            back.clicked += ui.Back;
        }

        public void Refresh(DateTimeOffset now)
        {
            HostModel model = ui.Host;
            bool busy = model.Status == HostStatus.Hosting || model.Status == HostStatus.Starting;
            online.EnableInClassList("br-selected", model.Reach == HostReach.Online);
            lan.EnableInClassList("br-selected", model.Reach == HostReach.LanOnly);
            Ui.Enable(online, !busy && model.TokenAvailable);
            Ui.Enable(lan, !busy);
            Ui.Show(tokenHint, !model.TokenAvailable);
            Ui.Text(tokenHint, model.TokenAvailable ? string.Empty : HostModel.NoTokenHint);
            Ui.Enable(port, !busy);
            Ui.Text(status, ui.HostStatusText());
            Ui.Show(start, !busy);
            Ui.Show(play, model.Status == HostStatus.Hosting);
            Ui.Show(stop, model.Status == HostStatus.Hosting);
            Ui.Show(back, !busy);
        }
    }

    /// <summary>Direct connect: the address of a LAN-only host and Connect.</summary>
    internal sealed class DirectConnectView
    {
        public DirectConnectView(VisualElement root, ClientUi ui)
        {
            TextField address = Ui.Require<TextField>(root, "direct-address");
            address.SetValueWithoutNotify(ui.DirectText);
            address.RegisterValueChangedCallback(change => ui.DirectText = change.newValue);
            Ui.Require<Button>(root, "direct-go").clicked += ui.DirectConnect;
            Ui.Require<Button>(root, "direct-back").clicked += ui.Back;
        }

        public void Refresh(DateTimeOffset now)
        {
        }
    }
}
