using System;
using System.Threading;
using System.Threading.Tasks;
using BeaconRush.Networking;
using BeaconRush.Session;
using PingCore.Core.Handshake;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// What differs between the hosting modes, for <see cref="GameServerRuntime"/>: the admission evidence and
    /// options, whether sessions come from allocations or are local, whether the host itself plays, and the
    /// hooks around listening, players and the end of a session. The runtime owns everything else (NGO, the
    /// approval, the session loop, the match). Main thread only.
    /// </summary>
    internal abstract class HostingModeBase : IDisposable
    {
        protected HostingModeBase(HostingSelection selection)
        {
            Selection = selection;
        }

        public HostingSelection Selection { get; }

        public GameHostingMode Mode => Selection.Mode;

        /// <summary>The evidence the approval reads for this mode.</summary>
        public abstract IAdmissionEvidence Evidence { get; }

        /// <summary>The UDP port of the reachability echo, or null without a heartbeat.</summary>
        public virtual int? QueryPort => null;

        /// <summary>Hosted: sessions are allocations. Every other mode runs local sessions.</summary>
        public virtual bool UsesAllocations => false;

        /// <summary>A listen host's own client is a player and holds a seat.</summary>
        public virtual bool HostPlays => false;

        /// <summary>The listen host's own display name.</summary>
        public virtual string HostDisplayName => null;

        /// <summary>
        /// The approval options for this mode, as the installed <see cref="ServerInstrumentation"/> leaves them
        /// (<see cref="ServerInstrumentation.ConfigureApproval"/>; by default exactly the game's own).
        /// </summary>
        public ApprovalOptions ApprovalOptions()
        {
            var options = new ApprovalOptions();
            ServerInstrumentation.Tell(i => i.ConfigureApproval(Mode, options));

            // The game's own fields, set after the instrumentation so it can never change them.
            options.ProtocolVersion = BeaconRushProtocol.Version;
            options.Mode = Selection.ApprovalMode;
            options.LanOnly = Selection.LanOnly;

            // Hosted only: the solo join. Beacon Rush ends every session it opens (lobby timeout, players left, match
            // complete), a claimed joiner that never arrives included, which is the obligation the opt-in carries.
            options.ClaimIdleSessions = UsesAllocations;
            return options;
        }

        /// <summary>Called once, before anything else, with the runtime that runs this mode.</summary>
        public virtual void Attach(GameServerRuntime runtime)
        {
        }

        /// <summary>Before NGO listens. False stops the boot (the process is stopping).</summary>
        public virtual Task<bool> BeforeListeningAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        /// <summary>After NGO listens: ready on a hosted game server, the heartbeat on a listed one.</summary>
        public virtual Task AfterListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Hosted: true when <paramref name="allocationId"/> is still the platform's current allocation.</summary>
        public virtual bool IsCurrentSession(string allocationId) => allocationId != null;

        /// <summary>Hosted: true when <paramref name="allocationId"/> is the current allocation and this game server's own self-allocation (the solo join's claim).</summary>
        public virtual bool IsSelfAllocatedSession(string allocationId) => false;

        /// <summary>The session's player count changed (<paramref name="phase"/>: <c>join</c> or <c>leave</c>).</summary>
        public virtual void OnPlayersChanged(int players, string phase)
        {
        }

        /// <summary>A phase of the open session began.</summary>
        public virtual void OnPhase(string allocationId, SessionPhase phase, SessionSettings settings, int players)
        {
        }

        /// <summary>The approval decided a connection.</summary>
        public virtual void OnDecided(AdmissionDecision decision, int players)
        {
        }

        /// <summary>The open session closed (ended, cleared or replaced).</summary>
        public virtual void OnSessionClosed()
        {
        }

        /// <summary>Carries out the end of an allocated session. Local sessions never call this.</summary>
        public virtual Task EndSessionAsync(SessionCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>The process (or the listen host) is stopping: tell the platform, start the delist. Must not block.</summary>
        public virtual void NotifyStopping()
        {
        }

        /// <summary>A stop the caller can wait for (a listen host the player stops while the app keeps running): the delist included.</summary>
        public virtual Task StopAsync()
        {
            NotifyStopping();
            return Task.CompletedTask;
        }

        public virtual void Dispose()
        {
        }
    }

    /// <summary>
    /// No Discovery at all: the unlisted dedicated game server (no local SDK endpoint, no token: a local smoke test)
    /// and the LAN-only listen host. Admits <c>lan</c> join tickets only (<see cref="LanAdmissionEvidence"/>); writes
    /// nothing anywhere, so it never integrates and never lists.
    /// </summary>
    internal sealed class LanMode : HostingModeBase
    {
        private readonly bool hostPlays;
        private readonly string hostName;

        public LanMode(HostingSelection selection, bool hostPlays, string hostName)
            : base(selection)
        {
            this.hostPlays = hostPlays;
            this.hostName = hostName;
        }

        public override IAdmissionEvidence Evidence { get; } = new LanAdmissionEvidence();

        public override bool HostPlays => hostPlays;

        public override string HostDisplayName => hostName;
    }

    /// <summary>
    /// The heartbeat tier: the self-hosted dedicated game server and the online listen host. Lists itself on a Discovery
    /// app (<see cref="HeartbeatTier"/>), reports its player count with every heartbeat, admits <c>reservation</c> join
    /// tickets checked with verify (<c>HeartbeatAdmissionEvidence</c>, as <see cref="ServerInstrumentation.WrapHeartbeatEvidence"/>
    /// hands it back), and delists on stop.
    /// </summary>
    internal sealed class HeartbeatMode : HostingModeBase
    {
        private readonly HeartbeatTier tier;
        private readonly bool hostPlays;
        private readonly string hostName;

        public HeartbeatMode(HostingSelection selection, HeartbeatTier tier, bool hostPlays, string hostName)
            : base(selection)
        {
            this.tier = tier ?? throw new ArgumentNullException(nameof(tier));
            this.hostPlays = hostPlays;
            this.hostName = hostName;
            IAdmissionEvidence evidence = new PingCore.Netcode.NGO.HeartbeatAdmissionEvidence(tier.Reporter);
            Evidence = ServerInstrumentation.Ask(i => i.WrapHeartbeatEvidence(evidence), evidence) ?? evidence;
        }

        public override IAdmissionEvidence Evidence { get; }

        public override int? QueryPort => tier.QueryPort;

        public override bool HostPlays => hostPlays;

        public override string HostDisplayName => hostName;

        public override async Task AfterListeningAsync(CancellationToken cancellationToken)
        {
            await tier.StartAsync(cancellationToken);
        }

        public override void OnPlayersChanged(int players, string phase) => tier.Reporter.SetPlayers(players);

        public override void NotifyStopping() => _ = tier.StopAsync();

        public override Task StopAsync() => tier.StopAsync();

        public override void Dispose() => tier.Dispose();
    }
}
