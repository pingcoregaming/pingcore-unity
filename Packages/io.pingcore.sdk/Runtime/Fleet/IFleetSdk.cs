using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>
    /// The local SDK shim: a PingCore-hosted game server's client for the local SDK endpoint
    /// that the supervisor (PID 1 in the same container) serves on
    /// <c>127.0.0.1:$AGONES_SDK_HTTP_PORT</c>. On any other build (a player, a self-hosted
    /// game server) <c>AGONES_SDK_HTTP_PORT</c> is unset and the shim is inert: every call
    /// returns at once without I/O.
    /// <para>
    /// Call it from the Unity main thread. Every await returns to the caller's context, so
    /// events are raised there too. No call throws for an HTTP or transport failure; each
    /// returns a result whose outcome says what happened.
    /// </para>
    /// <para>
    /// The integration rule: every write (POST, PATCH, DELETE) tells the supervisor the game
    /// talks to the SDK, and from then on it reports the game server <c>draining</c> to
    /// Discovery until <see cref="ReadyAsync"/>. A GET never integrates. So
    /// <see cref="StartAsync"/> reads only, and health pings start only after Ready.
    /// </para>
    /// </summary>
    public interface IFleetSdk : IDisposable
    {
        /// <summary>True when <c>AGONES_SDK_HTTP_PORT</c> was set to a port number (1 to 65535) at create.</summary>
        bool IsHosted { get; }

        /// <summary>The current lifecycle state.</summary>
        FleetState State { get; }

        /// <summary>The GameServer view from the last <c>GET /gameserver</c> or watch frame; null before the first.</summary>
        GameServerSnapshot Current { get; }

        /// <summary>The allocation on the view, or null when there is none.</summary>
        AllocationInfo CurrentAllocation { get; }

        /// <summary>Raised on every <see cref="State"/> transition, after the state has changed.</summary>
        event Action<FleetStateChange> StateChanged;

        /// <summary>Raised for every GameServer view the shim reads (the start read and every watch frame).</summary>
        event Action<GameServerSnapshot> GameServerChanged;

        /// <summary>Raised once per new <c>pingcore.io/allocation-id</c>; <see cref="State"/> is already <see cref="FleetState.InSession"/>.</summary>
        event Action<AllocationInfo> AllocationReceived;

        /// <summary>Raised when the allocation id leaves the view: <see cref="AllocationClearedReason.EndedByGame"/> after <see cref="EndSessionAsync"/>, otherwise <see cref="AllocationClearedReason.ClearedByPlatform"/>.</summary>
        event Action<AllocationCleared> AllocationCleared;

        /// <summary>
        /// Reads <c>GET /gameserver</c> (up to 30 tries, one second apart), then opens the watch
        /// stream. GETs only, so it does not integrate the game. Returns true once the view was
        /// read; false when inert, cancelled, stopping or after 30 failures (state
        /// <see cref="FleetState.Unreachable"/>; calling it again retries). Calling it while
        /// started returns true.
        /// </summary>
        Task<bool> StartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// <c>POST /ready</c>: the game server can take players. The state moves from
        /// <see cref="FleetState.NotReady"/> to <see cref="FleetState.Ready"/> once, on the Ready
        /// frame the supervisor writes just before it answers or on the 2xx, whichever the shim
        /// sees first; the health pings start on the 2xx. Call it once the game is listening for players.
        /// </summary>
        Task<FleetCallResult> ReadyAsync(CancellationToken cancellationToken);

        /// <summary>
        /// <c>PATCH /v1beta1/counters/{name}</c> with <c>{"count": count}</c>; the result carries
        /// the echoed count and capacity (the fleet block's capacity when the game never set one).
        /// <para>
        /// Write <c>players</c> only. Never write <c>sessions</c>: a count the game sets wins over
        /// the supervisor's own session tracking, so writing it freezes the session claim the
        /// matchmaker allocates against. The endpoint allows it; the platform then misbehaves.
        /// </para>
        /// </summary>
        Task<CounterResult> SetCounterAsync(string name, long count, CancellationToken cancellationToken);

        /// <summary><c>GET /v1beta1/counters/{name}</c>. A counter the fleet block does not declare and the game never set is <see cref="FleetCallOutcome.Rejected"/> (404).</summary>
        Task<CounterResult> GetCounterAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// <c>POST /v1/sessions/{allocationId}/ended</c>: the session is over. It also withdraws
        /// the joinable record and ends every backfill delivered into the session; ending a
        /// backfill id ends only that backfill. The state returns to Ready when the next watch
        /// frame no longer carries the allocation (the supervisor writes it just before it answers),
        /// and <see cref="AllocationCleared"/> reports <see cref="AllocationClearedReason.EndedByGame"/>.
        /// That holds when the answer is lost to a transport failure too; only a refusal
        /// (<see cref="FleetCallOutcome.Rejected"/> or <see cref="FleetCallOutcome.Unsupported"/>)
        /// makes a later clear <see cref="AllocationClearedReason.ClearedByPlatform"/>.
        /// </summary>
        Task<FleetCallResult> EndSessionAsync(string allocationId, CancellationToken cancellationToken);

        /// <summary>
        /// <c>POST /v1/sessions/{allocationId}/joinable</c>: announce open seats for backfill.
        /// <para>
        /// A 2xx (<see cref="JoinablePublishResult.LocallyAccepted"/>) means only that the
        /// supervisor stored the record and forwarded it to Discovery. It does not mean Discovery
        /// accepted it: on supervisor 1.3.4 a refusal by Discovery never reaches the game, and the
        /// only place it shows is the matchmaking inspector's <c>lastBackfillRefusal</c>, which
        /// needs a backend-scope token. Republish whenever seats change and at least every
        /// <c>ttlSeconds / 2</c>.
        /// </para>
        /// </summary>
        Task<JoinablePublishResult> PublishJoinableAsync(string allocationId, JoinableSessionRequest request, CancellationToken cancellationToken);

        /// <summary><c>DELETE /v1/sessions/{allocationId}/joinable</c>: stop backfill into the session. Idempotent: withdrawing twice, or with nothing published, is a 2xx.</summary>
        Task<FleetCallResult> WithdrawJoinableAsync(string allocationId, CancellationToken cancellationToken);

        /// <summary><c>GET /v1/backfills</c>: every backfill delivered into the running session and not yet ended, oldest first.</summary>
        Task<BackfillsResult> GetBackfillsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// <c>GET /pingcore/reservations/{reservationId}</c>: the hold a connecting player
        /// presents, if it is live on this game server. A <c>reservation not found</c> answer is
        /// asked again every <see cref="FleetSdkOptions.ReservationPoll"/> until
        /// <see cref="FleetSdkOptions.ReservationWait"/> has passed, with a final lookup at its end,
        /// because the push can trail the player.
        /// A served record whose <c>expiresAt</c> has passed is <see cref="ReservationLookupStatus.Expired"/>.
        /// The hosted path never calls Discovery's verify.
        /// </summary>
        Task<ReservationLookup> GetReservationAsync(string reservationId, CancellationToken cancellationToken);

        /// <summary><c>GET /pingcore/reservations</c>: every live hold on this game server, soonest-expiring first.</summary>
        Task<ReservationsResult> ListReservationsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// <c>POST /allocate</c>: the game claims this game server for a session of its own (Agones' <c>Allocate</c>).
        /// The supervisor tracks it as a session with the id <c>self-&lt;ms&gt;</c> (its clock in milliseconds) and an
        /// empty context, so the game server reads <c>in_session</c> with a <c>sessions</c> count of 1 and the matchmaker
        /// no longer allocates it while the fleet's <c>sessions</c> capacity is 1. It is a write, so it integrates the game.
        /// <para>
        /// The answer is <c>{}</c> and carries no id. After a 2xx the shim waits up to
        /// <see cref="FleetSdkOptions.SelfAllocationWait"/> for the watch frame (written before the answer), which moves
        /// <see cref="State"/> to <see cref="FleetState.InSession"/> and raises <see cref="AllocationReceived"/> with
        /// <see cref="AllocationInfo.IsSelfAllocated"/>; the result's <see cref="SelfAllocationResult.Allocation"/> is that
        /// allocation. End it with <see cref="EndSessionAsync"/> like any other.
        /// </para>
        /// <para>
        /// Nothing is sent while the view carries an allocation: the result is <see cref="FleetCallOutcome.Rejected"/>
        /// with status 0 and <see cref="SelfAllocationResult.AlreadyAllocated"/>, naming that allocation. The supervisor
        /// would accept the call and replace the current allocation with the self-allocation, cutting that session's
        /// players off. A platform allocation that arrives while the call is in flight is still replaced (the shim logs a
        /// warning when it sees one), and one that arrives while the self-allocation is current replaces it in turn (also a
        /// warning). Concurrent calls share one request. After a call whose answer was lost or whose frame did not come, no
        /// second request is sent for <see cref="FleetSdkOptions.SelfAllocationGrace"/> or until a frame carries an allocation
        /// (<see cref="SelfAllocationResult.AwaitingEarlier"/>). A self-allocation's id is guessable, so the
        /// handshake never admits a <c>match</c> join on it unless the game opts in
        /// (<c>ApprovalOptions.AllowSelfAllocatedJoins</c>); players come in on their reservations instead.
        /// </para>
        /// </summary>
        Task<SelfAllocationResult> AllocateSelfAsync(CancellationToken cancellationToken);

        /// <summary>
        /// <c>POST /shutdown</c>: drain this game server. The supervisor reports it
        /// <c>draining</c> and recycles the game process (it is stopped and started again, so
        /// <see cref="NotifyProcessStopping"/> follows from <c>Application.quitting</c>). On a 2xx
        /// the state is <see cref="FleetState.ShuttingDown"/> and the health pings stop.
        /// </summary>
        Task<FleetCallResult> ShutdownAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Call from <c>Application.quitting</c>. On a container stop the supervisor announces
        /// <c>draining</c> to Discovery, closes the local SDK endpoint and only then stops the
        /// game (stdin command if configured, then SIGTERM, then SIGKILL after 30 s), so by the
        /// time Unity quits new requests are refused (the open watch stream stays open until then).
        /// A refused call after the endpoint answered once is already
        /// <see cref="FleetCallOutcome.EndpointClosed"/>; this moves to
        /// <see cref="FleetState.Stopping"/>, stops the watch and the health pings, and makes
        /// every later failed call <see cref="FleetCallOutcome.EndpointClosed"/>, logged at info.
        /// Idempotent.
        /// </summary>
        void NotifyProcessStopping();
    }
}
