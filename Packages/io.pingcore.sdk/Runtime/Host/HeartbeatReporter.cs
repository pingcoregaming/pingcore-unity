using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;
using PingCore.Unity;
using UnityEngine;

namespace PingCore.Discovery.Host
{
    /// <summary>
    /// The heartbeat tier: keeps a self-hosted dedicated server or a listen host listed on a Discovery
    /// app, verifies the reservations players present, and delists on a clean stop. A PingCore-hosted
    /// game server never uses it: it reaches Discovery through the supervisor, so
    /// <see cref="StartAsync"/> refuses when <c>AGONES_SDK_HTTP_PORT</c> names a port.
    /// <para>
    /// Timing (<c>HeartbeatSchedule</c>): a beat every 30 s with up to 3 s jitter either way against
    /// Discovery's 90 s TTL; after a 429 the next beat waits <c>Retry-After</c>; after a 503, another
    /// 5xx or no answer it retries at 5, 10, then every 20 s; a warning is logged once two beats in a
    /// row are missed. A 409 can only come for a NEW serverId: <c>self_hosted_cap</c> or <c>ip_cap</c>
    /// with a <c>limit</c>. On the first beat <see cref="StartAsync"/> returns
    /// <see cref="HeartbeatStartOutcome.Refused"/>; later (after the entry lapsed in an outage) the loop
    /// stops and <see cref="Beat"/> reports it. Either way nothing more is sent.
    /// </para>
    /// <para>
    /// No call throws for an HTTP or transport failure. Call it from the Unity main thread: with the
    /// default scheduler and transport, results and events arrive there.
    /// </para>
    /// </summary>
    public sealed partial class HeartbeatReporter : IDisposable
    {
        private const string LogPrefix = "[PingCore.Host] ";
        private const int MaxPlayersCeiling = 1000000;
        private static readonly Regex ServerIdPattern = new Regex("^[A-Za-z0-9._:\\[\\]-]{1,128}$", RegexOptions.CultureInvariant);

        private readonly object gate = new object();
        private readonly string baseUrl;
        private readonly string token;
        private readonly bool tokenShipsInGame;
        private readonly IHttpTransport transport;
        private readonly IScheduler scheduler;
        private readonly string name;
        private readonly int gamePort;
        private readonly int? queryPort;
        private readonly bool queryPortConflict;
        private readonly int maxPlayers;
        private readonly string version;
        private readonly string ip;
        private readonly string configuredServerId;
        private readonly Action<HeartbeatLogEntry> log;
        private readonly Func<string, string> environmentVariable;
        private readonly Func<double> randomUnit;

        private DiscoveryCaller caller;
        private int players;
        private JObject meta;
        private bool changePending;
        private DateTimeOffset lastSendAt;
        private int failures;
        private bool missedWarned;
        private string serverId;
        private HeartbeatStatus status = HeartbeatStatus.NotStarted;
        private bool starting;
        private bool running;
        private bool stopped;
        private bool disposed;
        private CancellationTokenSource loopSource;
        private CancellationTokenSource wakeSource;
        private Task loopTask = Task.CompletedTask;
        private bool verifyFormWarned;
        private bool tcpQueryPortWarned;

        private HeartbeatReporter(HeartbeatReporterOptions options)
        {
            baseUrl = options.BaseUrl;
            token = options.Token;
            tokenShipsInGame = options.TokenShipsInGame;
            transport = options.Transport;
            scheduler = options.Scheduler ?? new AwaitableScheduler();
            name = options.Name == null ? null : options.Name.Trim();
            gamePort = options.GamePort;
            queryPort = options.SentQueryPort;
            queryPortConflict = options.OmitQueryPort && options.QueryPort.HasValue;
            maxPlayers = options.MaxPlayers;
            version = options.Version;
            meta = options.Meta == null ? null : (JObject)options.Meta.DeepClone();
            ip = options.Ip;
            configuredServerId = options.ServerId;
            log = options.Log ?? WriteToConsole;
            environmentVariable = options.EnvironmentVariable ?? Environment.GetEnvironmentVariable;
            randomUnit = options.RandomUnit ?? SecureIds.NextUnit;
        }

        /// <summary>Raised after every heartbeat send, the first included.</summary>
        public event Action<HeartbeatResult> Beat;

        /// <summary>The serverId Discovery recorded, from the first accepted heartbeat; null before it.</summary>
        public string ServerId
        {
            get
            {
                lock (gate)
                {
                    return serverId;
                }
            }
        }

        /// <summary>What Discovery last said about this game server, and the loop's state.</summary>
        public HeartbeatStatus Status
        {
            get
            {
                lock (gate)
                {
                    return status;
                }
            }
        }

        /// <summary>
        /// Creates a reporter. The options are copied, so later changes to the object have no effect;
        /// change the player count and meta with <see cref="SetPlayers"/> and <see cref="SetMeta"/>.
        /// Nothing is validated or sent until <see cref="StartAsync"/>.
        /// </summary>
        public static HeartbeatReporter Create(HeartbeatReporterOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return new HeartbeatReporter(options);
        }

        /// <summary>
        /// Sends the first heartbeat and, when Discovery accepts it, starts the loop. Refuses without
        /// sending when the local SDK endpoint is present, the options are invalid, or the reporter was
        /// started, stopped or disposed. A failed first heartbeat is not retried: call again.
        /// </summary>
        public async Task<HeartbeatStartResult> StartAsync(CancellationToken cancellationToken)
        {
            if (LocalSdkEndpointCheck.IsPresent(environmentVariable))
            {
                Write(HeartbeatLogLevel.Warning, "heartbeat", "AGONES_SDK_HTTP_PORT names a port, so this is a PingCore-hosted game server: it reaches Discovery through the supervisor and never heartbeats. Nothing was sent.", 0);
                return new HeartbeatStartResult(HeartbeatStartOutcome.LocalSdkEndpointPresent,
                    DiscoveryCallResult.Refused(DiscoveryReason.Unknown, "the local SDK endpoint is present (AGONES_SDK_HTTP_PORT)"), null, null);
            }

            lock (gate)
            {
                string refusal = disposed ? "the reporter is disposed"
                    : stopped ? "the reporter was stopped; create a new one"
                    : running || starting ? "the reporter is already started"
                    : null;
                if (refusal != null)
                {
                    return Failed(refusal);
                }

                starting = true;
            }

            try
            {
                string invalid = Validate();
                if (invalid != null)
                {
                    Write(HeartbeatLogLevel.Warning, "heartbeat", "not started: " + invalid, 0);
                    return Failed(invalid);
                }

                DiscoveryResult<HeartbeatResponse> result = await SendBeatAsync(cancellationToken);
                HeartbeatStep step = HeartbeatSchedule.Next(result, 0, randomUnit());
                if (result.IsOk)
                {
                    CancellationTokenSource source;
                    lock (gate)
                    {
                        if (disposed || stopped)
                        {
                            return Failed("the reporter was stopped while starting");
                        }

                        serverId = result.Value.ServerId;
                        failures = 0;
                        running = true;
                        status = AcceptedStatus(result.Value);
                        source = new CancellationTokenSource();
                        loopSource = source;
                    }

                    Write(HeartbeatLogLevel.Info, "heartbeat", "listed as " + result.Value.ServerId + " (verification " + result.Value.VerificationMode + ")", result.Status);
                    WarnIfTcpProbesTheQueryPort(result.Value);
                    RaiseBeat(new HeartbeatResult(result, 0, step.Delay, false));
                    Task loop = RunLoopAsync(step.Delay, source.Token);
                    lock (gate)
                    {
                        loopTask = loop;
                    }

                    return new HeartbeatStartResult(HeartbeatStartOutcome.Started, result, result.Value, null);
                }

                if (result.Outcome == DiscoveryOutcome.Conflict)
                {
                    int? limit = result.Error == null ? null : result.Error.Limit;
                    lock (gate)
                    {
                        stopped = true;
                        status = new HeartbeatStatus(false, true, null, null, null, null, null, 1);
                    }

                    Write(HeartbeatLogLevel.Warning, "heartbeat", "refused: " + result.ReasonWire + " (limit " + limit + "); nothing more is sent", result.Status);
                    RaiseBeat(new HeartbeatResult(result, 1, null, true));
                    return new HeartbeatStartResult(HeartbeatStartOutcome.Refused, result, null, limit);
                }

                Write(HeartbeatLogLevel.Warning, "heartbeat", "the first heartbeat failed: " + result + "; not started", result.Status);
                RaiseBeat(new HeartbeatResult(result, 1, null, false));
                return new HeartbeatStartResult(HeartbeatStartOutcome.Failed, result, null, null);
            }
            finally
            {
                lock (gate)
                {
                    starting = false;
                }
            }
        }

        /// <summary>Sets <c>players</c> (clamped to 0 to 1000000); a change is sent early, at most once per 5 s.</summary>
        public void SetPlayers(int value)
        {
            int clamped = value < 0 ? 0 : value > MaxPlayersCeiling ? MaxPlayersCeiling : value;
            CancellationTokenSource wake;
            lock (gate)
            {
                if (players == clamped)
                {
                    return;
                }

                players = clamped;
                changePending = true;
                wake = wakeSource;
            }

            Wake(wake);
        }

        /// <summary>Sets <c>meta</c> (copied; null sends none); sent early, at most once per 5 s.</summary>
        public void SetMeta(JObject value)
        {
            CancellationTokenSource wake;
            lock (gate)
            {
                if (JToken.DeepEquals(meta, value))
                {
                    return;
                }

                meta = value == null ? null : (JObject)value.DeepClone();
                changePending = true;
                wake = wakeSource;
            }

            Wake(wake);
        }
    }
}
