using System;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Discovery.Host
{
    /// <summary>
    /// Settings of one <see cref="HeartbeatReporter"/>. The token is a runtime value only: the game
    /// passes it in (Beacon Rush reads it from the <c>PINGCORE_DISCOVERY_TOKEN</c> environment
    /// variable), and the SDK never reads it from a settings asset, an argument or the environment
    /// itself, and never logs it. Validated by <see cref="HeartbeatReporter.StartAsync"/>, which
    /// answers <see cref="HeartbeatStartOutcome.Failed"/> with a local refusal for a bad value.
    /// </summary>
    public sealed class HeartbeatReporterOptions
    {
        /// <summary>Discovery's base URL, for example <c>https://discovery.pingcore.io</c>. Required.</summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// The Discovery app's heartbeat-scope <c>dsc_</c> token. Required; anything that does not
        /// start with <c>dsc_</c> is refused before sending, so a PingCore API key can never be sent
        /// to Discovery by mistake. Never logged, never in a result.
        /// </summary>
        public string Token { get; set; }

        /// <summary>
        /// True when <see cref="Token"/> is an open-registration app's heartbeat token that ships in the
        /// game. Discovery then answers verify with the verdict only and admits the seat itself; the
        /// reporter warns once when an answer's form says otherwise. It changes no verdict.
        /// </summary>
        public bool TokenShipsInGame { get; set; }

        /// <summary>The HTTP seam; null uses <c>UnityWebRequestTransport</c> with a 10 s call timeout.</summary>
        public IHttpTransport Transport { get; set; }

        /// <summary>The clock and delay; null uses <c>AwaitableScheduler</c> (Unity main thread).</summary>
        public IScheduler Scheduler { get; set; }

        /// <summary>The listing's <c>name</c>, 1 to 100 characters after trimming. Required.</summary>
        public string Name { get; set; }

        /// <summary>The game port players connect to (<c>port</c>), 1 to 65535. Required.</summary>
        public int GamePort { get; set; }

        /// <summary>
        /// The port Discovery probes (<c>queryPort</c>), where <see cref="UdpEchoResponder"/> listens;
        /// 1 to 65535. Null means <see cref="GamePort"/> plus one, unless <see cref="OmitQueryPort"/>.
        /// </summary>
        public int? QueryPort { get; set; }

        /// <summary>
        /// Sends no <c>queryPort</c>, so Discovery probes the game port itself. An app whose verification
        /// mode is <c>tcp</c> dials <c>queryPort</c> when one is sent, and the UDP echo responder cannot answer a
        /// TCP connect, so a game that listens on TCP and runs no echo responder sets this. Leave
        /// <see cref="QueryPort"/> null with it. The reporter warns once when Discovery answers <c>tcp</c>
        /// while a <c>queryPort</c> is sent.
        /// </summary>
        public bool OmitQueryPort { get; set; }

        /// <summary><c>maxPlayers</c>, 0 to 1000000.</summary>
        public int MaxPlayers { get; set; }

        /// <summary>The game's own version string (<c>version</c>), or null to send none.</summary>
        public string Version { get; set; }

        /// <summary>Initial studio-defined filterable fields (<c>meta</c>), or null to send none. Change later with <see cref="HeartbeatReporter.SetMeta"/>.</summary>
        public JObject Meta { get; set; }

        /// <summary>The public address to list (<c>ip</c>), or null to let Discovery use the observed source address.</summary>
        public string Ip { get; set; }

        /// <summary>A stable id (<c>serverId</c>), or null to let Discovery use <c>ip:port</c>. The id the first answer records is used from then on.</summary>
        public string ServerId { get; set; }

        /// <summary>Diagnostics; null writes to the Unity console. Entries never carry the token.</summary>
        public Action<HeartbeatLogEntry> Log { get; set; }

        /// <summary>Test seam: reads an environment variable. Null reads the process environment.</summary>
        internal Func<string, string> EnvironmentVariable { get; set; }

        /// <summary>Test seam: the jitter source, values in [0, 1). Null uses <c>SecureIds.NextUnit</c>.</summary>
        internal Func<double> RandomUnit { get; set; }

        /// <summary>The port the echo responder binds: <see cref="QueryPort"/>, else <see cref="GamePort"/> plus one.</summary>
        public int EffectiveQueryPort => QueryPort ?? (GamePort + 1);

        /// <summary>The <c>queryPort</c> that will be sent: null (not sent) with <see cref="OmitQueryPort"/>, else <see cref="EffectiveQueryPort"/>.</summary>
        public int? SentQueryPort => OmitQueryPort ? (int?)null : EffectiveQueryPort;
    }
}
