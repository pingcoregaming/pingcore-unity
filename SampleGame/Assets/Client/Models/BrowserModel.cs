using System;
using System.Collections.Generic;
using PingCore.Discovery.Client;
using PingCore.Discovery.Client.Wire;

namespace BeaconRush.Client.Models
{
    /// <summary>How the browser orders its rows.</summary>
    public enum BrowserSort
    {
        /// <summary>Most players first (descending) or fewest first.</summary>
        Players,

        /// <summary>By name.</summary>
        Name,

        /// <summary>By the measured latency to the game server's location; needs a latency measurement.</summary>
        Latency,
    }

    /// <summary>One row of the server browser.</summary>
    public sealed class BrowserRow
    {
        /// <summary>Creates a row.</summary>
        public BrowserRow(string serverId, string name, int players, int maxPlayers, string version, string location, int? latencyMs, string ip, int port)
        {
            ServerId = serverId;
            Name = name;
            Players = players;
            MaxPlayers = maxPlayers;
            Version = version;
            Location = location;
            LatencyMs = latencyMs;
            Ip = ip;
            Port = port;
        }

        /// <summary>Discovery's id of the game server.</summary>
        public string ServerId { get; }

        /// <summary>The listed name.</summary>
        public string Name { get; }

        /// <summary>Players on it now.</summary>
        public int Players { get; }

        /// <summary>Its seat count.</summary>
        public int MaxPlayers { get; }

        /// <summary>The build version it reports, or null.</summary>
        public string Version { get; }

        /// <summary><c>meta.location</c>, or null when it reports none.</summary>
        public string Location { get; }

        /// <summary>The measured median to <see cref="Location"/>, or null when unmeasured.</summary>
        public int? LatencyMs { get; }

        /// <summary>Its listed address.</summary>
        public string Ip { get; }

        /// <summary>Its listed game port.</summary>
        public int Port { get; }

        /// <summary>True when it has a free seat.</summary>
        public bool HasFreeSeat => Players < MaxPlayers;

        /// <summary>The players column, for example <c>3/8</c>.</summary>
        public string PlayersText => Players + "/" + MaxPlayers;

        /// <summary>The latency column, for example <c>24 ms</c>, or <c>-</c>.</summary>
        public string LatencyText => LatencyMs.HasValue ? LatencyMs.Value + " ms" : "-";
    }

    /// <summary>The state of one browser tab (one Discovery app).</summary>
    public sealed class BrowserTab
    {
        internal BrowserTab(ClientApp app)
        {
            App = app;
        }

        /// <summary>The app this tab lists.</summary>
        public ClientApp App { get; }

        /// <summary>The rows of the current page.</summary>
        public IReadOnlyList<BrowserRow> Rows { get; internal set; } = Array.Empty<BrowserRow>();

        /// <summary>Matches after filtering, before paging.</summary>
        public int TotalServers { get; internal set; }

        /// <summary>The offset of the current page.</summary>
        public int Offset { get; internal set; }

        /// <summary>True when more matches follow the current page.</summary>
        public bool HasMore { get; internal set; }

        /// <summary>The last failure, shown above the list; null after a good answer.</summary>
        public string Error { get; internal set; }

        /// <summary>True while a list request is in flight.</summary>
        public bool Loading { get; internal set; }

        /// <summary>When the last list request was sent, or null before the first.</summary>
        public DateTimeOffset? LastRequestAt { get; internal set; }

        /// <summary>True once a page has arrived.</summary>
        public bool Loaded { get; internal set; }
    }

    /// <summary>
    /// The server browser, pure: two tabs (Fleet and Community), filters, sort, paging and the refresh
    /// limit. The view asks <see cref="TryBeginRefresh"/> for the query to send, then reports the answer
    /// with <see cref="ApplyPage"/> or <see cref="ApplyFailure"/>. A tab sends at most one list request
    /// every <see cref="MinRefreshInterval"/>, however often the player clicks.
    /// </summary>
    public sealed class BrowserModel
    {
        /// <summary>Rows per page.</summary>
        public const int PageSize = 20;

        /// <summary>The shortest gap between two list requests of one tab.</summary>
        public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(5);

        private readonly Dictionary<ClientApp, BrowserTab> tabs = new Dictionary<ClientApp, BrowserTab>();
        private IReadOnlyDictionary<string, int> latency = new Dictionary<string, int>();

        /// <summary>Creates the model for a game with the given network protocol version.</summary>
        public BrowserModel(int protocolVersion)
        {
            ProtocolVersion = protocolVersion;
            tabs[ClientApp.Fleet] = new BrowserTab(ClientApp.Fleet);
            tabs[ClientApp.Community] = new BrowserTab(ClientApp.Community);
        }

        /// <summary>The game's protocol version, the value of the <c>meta.proto</c> filter.</summary>
        public int ProtocolVersion { get; }

        /// <summary>The tab on screen.</summary>
        public ClientApp ActiveTab { get; set; } = ClientApp.Fleet;

        /// <summary>Only game servers with a free seat (<c>hasSlots=true</c>).</summary>
        public bool HasSlotsOnly { get; private set; } = true;

        /// <summary>Only game servers whose <c>meta.proto</c> is <see cref="ProtocolVersion"/>.</summary>
        public bool SameProtocolOnly { get; private set; }

        /// <summary>The name search, empty for none.</summary>
        public string Search { get; private set; } = string.Empty;

        /// <summary>The sort key.</summary>
        public BrowserSort Sort { get; private set; } = BrowserSort.Players;

        /// <summary>True for descending order.</summary>
        public bool SortDescending { get; private set; } = true;

        /// <summary>The measured medians per location id.</summary>
        public IReadOnlyDictionary<string, int> Latency => latency;

        /// <summary>The state of one tab.</summary>
        public BrowserTab Tab(ClientApp app)
        {
            if (!tabs.TryGetValue(app, out BrowserTab tab))
            {
                throw new ArgumentOutOfRangeException(nameof(app), "the browser has Fleet and Community tabs only");
            }

            return tab;
        }

        /// <summary>Sets the latency medians; the latency column and the latency sort use them.</summary>
        public void SetLatency(IReadOnlyDictionary<string, int> medians)
        {
            latency = medians ?? new Dictionary<string, int>();
        }

        /// <summary>Changes the filters; a change returns every tab to its first page.</summary>
        public void SetFilters(bool hasSlotsOnly, bool sameProtocolOnly, string search)
        {
            string trimmed = (search ?? string.Empty).Trim();
            if (hasSlotsOnly == HasSlotsOnly && sameProtocolOnly == SameProtocolOnly && trimmed == Search)
            {
                return;
            }

            HasSlotsOnly = hasSlotsOnly;
            SameProtocolOnly = sameProtocolOnly;
            Search = trimmed.Length > 64 ? trimmed.Substring(0, 64) : trimmed;
            ResetOffsets();
        }

        /// <summary>Changes the sort; a change returns every tab to its first page.</summary>
        public void SetSort(BrowserSort sort, bool descending)
        {
            if (sort == Sort && descending == SortDescending)
            {
                return;
            }

            Sort = sort;
            SortDescending = descending;
            ResetOffsets();
        }

        /// <summary>Moves a tab to its next page (only when one follows). The next refresh loads it.</summary>
        public bool NextPage(ClientApp app)
        {
            BrowserTab tab = Tab(app);
            if (!tab.HasMore)
            {
                return false;
            }

            tab.Offset += PageSize;
            return true;
        }

        /// <summary>Moves a tab to its previous page. The next refresh loads it.</summary>
        public bool PreviousPage(ClientApp app)
        {
            BrowserTab tab = Tab(app);
            if (tab.Offset == 0)
            {
                return false;
            }

            tab.Offset = Math.Max(0, tab.Offset - PageSize);
            return true;
        }

        /// <summary>How long until the tab may send again; zero when it may now.</summary>
        public TimeSpan RefreshWait(ClientApp app, DateTimeOffset now)
        {
            BrowserTab tab = Tab(app);
            if (tab.LastRequestAt == null)
            {
                return TimeSpan.Zero;
            }

            TimeSpan left = tab.LastRequestAt.Value + MinRefreshInterval - now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        /// <summary>True when the tab is idle and its last request is at least <see cref="MinRefreshInterval"/> old.</summary>
        public bool CanRefresh(ClientApp app, DateTimeOffset now)
        {
            return !Tab(app).Loading && RefreshWait(app, now) == TimeSpan.Zero;
        }

        /// <summary>When the tab may send, marks it loading and returns the query; otherwise false and no query.</summary>
        public bool TryBeginRefresh(ClientApp app, DateTimeOffset now, out ServerListQuery query)
        {
            query = null;
            if (!CanRefresh(app, now))
            {
                return false;
            }

            BrowserTab tab = Tab(app);
            tab.Loading = true;
            tab.LastRequestAt = now;
            query = BuildQuery(app);
            return true;
        }

        /// <summary>The list query for a tab's current page, filters and sort. Pure.</summary>
        public ServerListQuery BuildQuery(ClientApp app)
        {
            BrowserTab tab = Tab(app);
            var query = new ServerListQuery().Page(PageSize, tab.Offset);
            if (HasSlotsOnly)
            {
                query.HasSlots(true);
            }

            if (Search.Length > 0)
            {
                query.Search(Search);
            }

            if (SameProtocolOnly)
            {
                query.Meta("proto", (long)ProtocolVersion);
            }

            bool haveLatency = latency.Count > 0;
            if (haveLatency)
            {
                query.WithLatency(latency);
            }

            switch (Sort)
            {
                case BrowserSort.Name:
                    query.SortBy(ServerSort.Name, SortDescending);
                    break;
                case BrowserSort.Latency when haveLatency:
                    query.SortBy(ServerSort.Latency, SortDescending);
                    break;
                default:
                    // A latency sort before any measurement falls back to the default order, most players first.
                    query.SortBy(ServerSort.Players, Sort == BrowserSort.Players ? SortDescending : true);
                    break;
            }

            return query;
        }

        /// <summary>Records a good answer for a tab.</summary>
        public void ApplyPage(ClientApp app, ServerPage page)
        {
            BrowserTab tab = Tab(app);
            tab.Loading = false;
            tab.Loaded = true;
            tab.Error = null;
            if (page == null)
            {
                tab.Rows = Array.Empty<BrowserRow>();
                tab.TotalServers = 0;
                tab.HasMore = false;
                return;
            }

            var rows = new List<BrowserRow>(page.Servers.Count);
            foreach (PublicServer server in page.Servers)
            {
                rows.Add(ToRow(server, latency));
            }

            tab.Rows = rows;
            tab.TotalServers = page.TotalServers;
            tab.Offset = page.Offset;
            tab.HasMore = page.HasMore;
        }

        /// <summary>Records a failed answer for a tab; the rows of the last good page stay.</summary>
        public void ApplyFailure(ClientApp app, string message)
        {
            BrowserTab tab = Tab(app);
            tab.Loading = false;
            tab.Error = string.IsNullOrEmpty(message) ? "the list did not answer" : message;
        }

        /// <summary>One row from a listed game server, its latency from <c>meta.location</c>. Pure.</summary>
        public static BrowserRow ToRow(PublicServer server, IReadOnlyDictionary<string, int> medians)
        {
            string location = null;
            if (server.Meta != null && server.Meta.TryGetValue("location", out Newtonsoft.Json.Linq.JToken value)
                && value.Type == Newtonsoft.Json.Linq.JTokenType.String)
            {
                location = (string)value;
            }

            int? ms = null;
            if (location != null && medians != null && medians.TryGetValue(location, out int median))
            {
                ms = median;
            }

            return new BrowserRow(server.ServerId, server.Name, server.Players, server.MaxPlayers, server.Version, location, ms, server.Ip, server.Port);
        }

        private void ResetOffsets()
        {
            foreach (BrowserTab tab in tabs.Values)
            {
                tab.Offset = 0;
            }
        }
    }
}
