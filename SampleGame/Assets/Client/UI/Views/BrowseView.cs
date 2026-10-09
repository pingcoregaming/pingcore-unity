using System;
using System.Collections.Generic;
using BeaconRush.Client.Models;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI.Views
{
    /// <summary>
    /// Browse: the Fleet and Community tabs, the filters (free seats, same protocol, name search), the sort, the page of game
    /// servers with a Join button each, and paging. The <see cref="BrowserModel"/> owns the refresh limit; this view asks for
    /// a page when a tab first opens and when the player presses Refresh or turns a page.
    /// </summary>
    internal sealed class BrowseView
    {
        private readonly ClientUi ui;
        private readonly Button tabFleet;
        private readonly Button tabCommunity;
        private readonly Label unconfigured;
        private readonly VisualElement body;
        private readonly Toggle slots;
        private readonly Toggle proto;
        private readonly TextField search;
        private readonly Dictionary<BrowserSort, Button> sorts = new Dictionary<BrowserSort, Button>();
        private readonly Button refresh;
        private readonly Label error;
        private readonly Label summary;
        private readonly ScrollView list;
        private readonly Button previous;
        private readonly Button next;
        private IReadOnlyList<BrowserRow> shownRows;
        private bool shownReserving;

        public BrowseView(VisualElement root, ClientUi ui)
        {
            this.ui = ui;
            tabFleet = Ui.Require<Button>(root, "browse-tab-fleet");
            tabCommunity = Ui.Require<Button>(root, "browse-tab-community");
            unconfigured = Ui.Require<Label>(root, "browse-unconfigured");
            body = Ui.Require<VisualElement>(root, "browse-body");
            slots = Ui.Require<Toggle>(root, "browse-slots");
            proto = Ui.Require<Toggle>(root, "browse-proto");
            search = Ui.Require<TextField>(root, "browse-search");
            refresh = Ui.Require<Button>(root, "browse-refresh");
            error = Ui.Require<Label>(root, "browse-error");
            summary = Ui.Require<Label>(root, "browse-summary");
            list = Ui.Require<ScrollView>(root, "browse-list");
            previous = Ui.Require<Button>(root, "browse-prev");
            next = Ui.Require<Button>(root, "browse-next");
            sorts[BrowserSort.Players] = Ui.Require<Button>(root, "browse-sort-players");
            sorts[BrowserSort.Name] = Ui.Require<Button>(root, "browse-sort-name");
            sorts[BrowserSort.Latency] = Ui.Require<Button>(root, "browse-sort-latency");

            BrowserModel model = ui.Browser;
            proto.label = "Protocol " + model.ProtocolVersion + " only";
            slots.SetValueWithoutNotify(model.HasSlotsOnly);
            proto.SetValueWithoutNotify(model.SameProtocolOnly);
            search.SetValueWithoutNotify(model.Search);
            slots.RegisterValueChangedCallback(_ => ApplyFilters());
            proto.RegisterValueChangedCallback(_ => ApplyFilters());
            search.RegisterValueChangedCallback(_ => ApplyFilters());
            tabFleet.clicked += () => model.ActiveTab = ClientApp.Fleet;
            tabCommunity.clicked += () => model.ActiveTab = ClientApp.Community;
            foreach (KeyValuePair<BrowserSort, Button> pair in sorts)
            {
                BrowserSort sort = pair.Key;
                pair.Value.clicked += () => model.SetSort(sort, model.Sort == sort ? !model.SortDescending : sort == BrowserSort.Players);
            }

            refresh.clicked += () => ui.RefreshBrowse(model.ActiveTab);
            previous.clicked += () =>
            {
                if (model.PreviousPage(model.ActiveTab))
                {
                    ui.RefreshBrowse(model.ActiveTab);
                }
            };
            next.clicked += () =>
            {
                if (model.NextPage(model.ActiveTab))
                {
                    ui.RefreshBrowse(model.ActiveTab);
                }
            };
            Ui.Require<Button>(root, "browse-back").clicked += ui.Back;
        }

        public void Refresh(DateTimeOffset now)
        {
            BrowserModel model = ui.Browser;
            ClientApp app = model.ActiveTab;
            tabFleet.EnableInClassList("br-selected", app == ClientApp.Fleet);
            tabCommunity.EnableInClassList("br-selected", app == ClientApp.Community);
            bool configured = ui.Services.IsConfigured(app);
            Ui.Show(unconfigured, !configured);
            Ui.Show(body, configured);
            if (!configured)
            {
                Ui.Text(unconfigured, "The " + ClientApps.ToName(app) + " Discovery app is not configured in Settings/PingCoreClientSettings.asset.");
                return;
            }

            foreach (KeyValuePair<BrowserSort, Button> pair in sorts)
            {
                bool selected = model.Sort == pair.Key;
                pair.Value.EnableInClassList("br-selected", selected);
                Ui.Text(pair.Value, pair.Key + (selected ? (model.SortDescending ? " ↓" : " ↑") : string.Empty));
            }

            TimeSpan wait = model.RefreshWait(app, now);
            Ui.Text(refresh, wait > TimeSpan.Zero ? "Refresh (" + Math.Ceiling(wait.TotalSeconds) + " s)" : "Refresh");
            Ui.Enable(refresh, model.CanRefresh(app, now));

            BrowserTab tab = model.Tab(app);
            if (!tab.Loaded && !tab.Loading && model.CanRefresh(app, now))
            {
                ui.RefreshBrowse(app);
            }

            Ui.Show(error, tab.Error != null);
            Ui.Text(error, tab.Error != null ? "The list did not load: " + tab.Error : string.Empty);
            Ui.Text(summary, tab.Loading ? "Loading..." : tab.TotalServers + " game servers; showing "
                + (tab.Rows.Count == 0 ? 0 : tab.Offset + 1) + "-" + (tab.Offset + tab.Rows.Count));
            Ui.Enable(previous, tab.Offset > 0);
            Ui.Enable(next, tab.HasMore);
            if (!ReferenceEquals(shownRows, tab.Rows) || shownReserving != ui.Reserving)
            {
                Rebuild(app, tab.Rows);
            }
        }

        private void ApplyFilters()
        {
            ui.Browser.SetFilters(slots.value, proto.value, search.value);
        }

        private void Rebuild(ClientApp app, IReadOnlyList<BrowserRow> rows)
        {
            shownRows = rows;
            shownReserving = ui.Reserving;
            list.Clear();
            if (rows.Count == 0)
            {
                var empty = new Label("No game servers match. Change the filters or refresh.");
                empty.AddToClassList("br-hint");
                list.Add(empty);
                return;
            }

            foreach (BrowserRow row in rows)
            {
                var line = new VisualElement();
                line.AddToClassList("br-list-row");
                line.Add(Cell(row.Name, "br-col-name"));
                line.Add(Cell(row.PlayersText, "br-col-num"));
                line.Add(Cell(row.LatencyText, "br-col-num"));
                line.Add(Cell(row.Version ?? "-", "br-col-version"));
                BrowserRow chosen = row;
                var join = new Button(() => ui.JoinListed(app, chosen)) { text = row.HasFreeSeat ? "Join" : "Full" };
                join.AddToClassList("br-chip");
                join.SetEnabled(row.HasFreeSeat && !ui.Reserving);
                line.Add(join);
                list.Add(line);
            }
        }

        private static Label Cell(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }
    }
}
