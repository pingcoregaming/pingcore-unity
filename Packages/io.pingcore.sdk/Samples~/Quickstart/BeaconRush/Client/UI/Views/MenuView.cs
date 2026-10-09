using System;
using BeaconRush.Client.Models;
using BeaconRush.Networking;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI.Views
{
    /// <summary>
    /// The main menu: the missing-infrastructure banner (the SDK check's message, the Editor's exact reason under it, and
    /// Check again), display name, Quick play, Find match, Browse, Host a game, Direct connect, Join by game server id, Quit.
    /// </summary>
    internal sealed class MenuView
    {
        private const string NotConfigured = " (app not configured)";

        private readonly ClientUi ui;
        private readonly TextField name;
        private readonly Label profile;
        private readonly Button quick;
        private readonly Button find;
        private readonly Button reservation;
        private readonly VisualElement infraBox;
        private readonly Label infra;
        private readonly Label infraDetail;
        private readonly Button infraRetry;

        public MenuView(VisualElement root, ClientUi ui)
        {
            this.ui = ui;
            name = Ui.Require<TextField>(root, "menu-name");
            profile = Ui.Require<Label>(root, "menu-profile");
            quick = Ui.Require<Button>(root, "menu-quick");
            find = Ui.Require<Button>(root, "menu-find");
            reservation = Ui.Require<Button>(root, "menu-reservation");
            infraBox = Ui.Require<VisualElement>(root, "menu-infra-box");
            infra = Ui.Require<Label>(root, "menu-infra");
            infraDetail = Ui.Require<Label>(root, "menu-infra-detail");
            infraRetry = Ui.Require<Button>(root, "menu-infra-retry");
            infraRetry.clicked += ui.CheckInfrastructure;
            Ui.Text(Ui.Require<Label>(root, "menu-version"), "Protocol " + BeaconRushProtocol.Version + "  |  queue " + BeaconRushProtocol.Queue);

            name.SetValueWithoutNotify(ui.DisplayName);
            name.RegisterValueChangedCallback(change => ui.DisplayName = change.newValue);
            name.RegisterCallback<FocusOutEvent>(_ => name.SetValueWithoutNotify(ui.DisplayName));
            quick.clicked += ui.QuickPlay;
            find.clicked += () => ui.Navigate(ClientScreen.FindMatch);
            Ui.Require<Button>(root, "menu-browse").clicked += () => ui.Navigate(ClientScreen.Browse);
            Ui.Require<Button>(root, "menu-host").clicked += () => ui.Navigate(ClientScreen.Host);
            Ui.Require<Button>(root, "menu-direct").clicked += () => ui.Navigate(ClientScreen.DirectConnect);
            reservation.clicked += () => ui.Navigate(ClientScreen.JoinReservation);
            Ui.Require<Button>(root, "menu-quit").clicked += ui.QuitGame;
        }

        public void Refresh(DateTimeOffset now)
        {
            bool fleet = ui.Services.IsConfigured(ClientApp.Fleet);
            Ui.Text(profile, "Player profile: " + ui.ProfileLabel);
            Ui.Text(quick, !fleet ? "Quick play" + NotConfigured : ui.QuickJoining ? "Finding a game..." : "Quick play");
            Ui.Enable(quick, fleet && !ui.QuickJoining && !ui.Reserving);
            Ui.Text(find, fleet ? "Find match" : "Find match" + NotConfigured);
            Ui.Enable(find, fleet && !ui.QuickJoining);
            Ui.Enable(reservation, fleet && !ui.QuickJoining);

            string headline = InfrastructureBanner.Headline(ui.Infrastructure);
            string detail = InfrastructureBanner.Detail(ui.Infrastructure);
            Ui.Show(infraBox, headline != null);
            Ui.Text(infra, headline);
            Ui.Show(infraDetail, detail != null);
            Ui.Text(infraDetail, detail);
            Ui.Text(infraRetry, ui.CheckingInfrastructure ? "Checking..." : "Check again");
            Ui.Enable(infraRetry, !ui.CheckingInfrastructure);
        }
    }
}
