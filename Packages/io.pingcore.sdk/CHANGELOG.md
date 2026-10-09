# Changelog

All notable changes to `io.pingcore.sdk` are recorded here, in the form of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/). Both PingCore packages share one version and one tag.

<!-- Convention: the version being prepared is "## [X.Y.Z] - Unreleased"; the release is not
published until it reads "## [X.Y.Z] - YYYY-MM-DD". After a release, changes for the next one go under
a new "## [Unreleased]" heading at the top. Every heading has a link reference at the end of this file:
[Unreleased] compares the newest tag with HEAD, [X.Y.Z] points at the release tag. -->

## [0.1.1] - 2026-10-09

### Changed

- The documentation moved to pingcore.io (https://pingcore.io/docs/unity/unity-overview), which is now its single source. This package's README keeps what the package is, how to install it and links to those pages. No code changes.

## [0.1.0] - 2026-10-09

The first release: the local SDK shim for PingCore-hosted game servers (`PingCore.Fleet`), the Discovery
client for players (`PingCore.Discovery.Client`), the heartbeat tier for self-hosted dedicated game servers
and listen hosts (`PingCore.Discovery.Host`), the connect handshake and its Netcode for GameObjects
connection approval (`PingCore.Netcode.NGO`), and the engine-neutral pieces under them (`PingCore.Core`).
Builds are verified with Mono; IL2CPP and WebGL builds are not yet verified.

### Packaging

- Assemblies `PingCore.Core` (no engine reference, compiles under plain netstandard2.1),
  `PingCore.Unity`, `PingCore.Discovery.Client`, `PingCore.Discovery.Host`, `PingCore.Fleet` and
  `PingCore.Netcode.NGO` (compiled only when Netcode for GameObjects 2.x is installed). Dependencies
  point towards Core; Client and Host never reference each other or Fleet.
- Package dependencies: `com.unity.nuget.newtonsoft-json` and the built-in module
  `com.unity.modules.unitywebrequest`. Netcode for GameObjects is optional.
- `link.xml` keeps every runtime assembly whole, `PingCore.Netcode.NGO` with `ignoreIfMissing`.

### Core (`PingCore.Core`)

- `WireContractAttribute`, `WireResponse`, `ErrorEnvelope` (a contract for every error status the
  client and host branch on), `DiscoveryReason` and `DiscoveryReasons.Parse`, `PingCoreJson.Settings`,
  the `IHttpTransport` and `IScheduler` interfaces, `PreserveAttribute`, `PingCoreSdkInfo.Version`.
- The Discovery call pipeline (`PingCore.Core.Discovery`): `DiscoveryCaller` (sends one
  `DiscoveryRequest` through `IHttpTransport`, classifies the answer, parses the envelope, reads
  `Retry-After` and the `RateLimit-*` headers; never logs a header, body or URL), `DiscoveryOutcome`,
  `DiscoveryCallResult` and `DiscoveryResult<T>`, `RateLimitInfo`, the pure `RetryGovernor` (429 waits
  `Retry-After`; 503, unexpected and transport back off 2, 4, 8, capped at 10 s), `SecureIds`
  (`NewId128`, 128 random bits as 22 base64url characters; `Ref`, the first 12 hex of a SHA-256),
  `IPlayerTokenStore` with `StoredPlayerToken`, `PlayerTokenStoreKeys`
  (`pingcore.playerToken.<profile>.<dscp>`) and `MemoryTokenStore`, and `PingCoreTransportException`.
- The connect handshake (`PingCore.Core.Handshake`, engine-neutral so any netcode can reuse it):
  `JoinTicket` (the v1 schema; factories `ForReservation`, `ForMatch`, `ForBackfill`, `ForLan`;
  `TicketRef` is the only form of the ticket id it prints), `JoinTicketCodec.Encode` and `TryEncode`
  (compact, schema order, refuses over 1024 bytes) and `Decode` (strict UTF-8 and RFC 8259 JSON, no
  duplicate or unknown keys, `v` first, lengths in code points), `JoinRejectReason` with its literal
  table (19 rejections), `HostingMode`, `RosterEntry`, `AdmissionFacts`, `JoinAdmission.Decide` (the
  decision table of `docs/connect-handshake.md`, pure), `AdmissionLedger` (party size per allocation and
  ticket, seats per reservation, players per rosterless allocation, one connection per player, released
  on disconnect), `IAdmissionEvidence` with `LanAdmissionEvidence`, `IAdmissionGate` with `AdmitAllGate`,
  `ApprovalOptions` and `AdmissionPipeline` (decode, protocol, stopping, the evidence raced against the
  10 s deadline, the table, the game's gate, the ledger hold in one step).
- The decision rules: roster entries match on the ticket id alone, up to `partySize` distinct players;
  a `match` into an allocation the matchmaker did not make and that carries no roster (a backend
  allocation or a supervisor self-allocation) is admitted on its `allocationId` alone, up to the
  `players` counter's capacity or `ApprovalOptions.MaxPlayers`; a matchmaker allocation with an empty
  roster still refuses `not_in_roster`; a hold naming no players, hosted or from a detailed verify
  answer, admits up to its `seats` (`roster_full` after that); `duplicate_player` applies to every kind;
  evidence of the wrong sort for the hosting mode fails closed as `reservation_unverifiable`.

### Unity (`PingCore.Unity`)

- `UnityWebRequestTransport` (main thread, per-call timeout, no redirects, every status returned with
  its body and headers), the public `AwaitableScheduler`, and `PlayerPrefsTokenStore`, the client's
  default token store.

### Discovery client (`PingCore.Discovery.Client`)

- `DiscoveryClient` for one Discovery app, created from `DiscoveryClientOptions` or the
  `PingCoreClientSettings` asset. It takes only a `dscp_` public id and refuses any option shaped like
  a `usr_`, `sys_`, `cdnpush_` or `dsc_` credential.
- The server list with the pure `ServerListQuery` (meta operators, sort, latency, paging) and
  `ServerPage`; locations (cached 5 min) and `MeasureLatencyAsync` (cached 10 min) on `LatencyProbe`: a
  discarded warm-up, the median of 5 pongs, sub-millisecond reported as 1, failures omitted, the pong
  timestamp read on the completing thread, behind `IWebSocketEchoTransport` (default `ClientWebSocket`, and
  the browser's WebSocket in a WebGL player, below).
- `PlayerTokenCache`: one anonymous token per app per profile, persisted, single-flight issuance at
  most once per 6 s per process and never inside a `Retry-After`, studio-signed tokens
  (`SetSignedToken`) and a provider hook (`SetSignedTokenProvider`); every authorised call drops a token
  Discovery rejects with a 401 player-token reason, gets a new one once and resends the same request;
  403 and 503 token refusals are typed results, never retried.
- `ReserveAsync` and `QuickJoinAsync` mint the reservation id or idempotency key and reuse it on their
  own retry; `GetReservationAsync`, `ReleaseReservationAsync`.
- `SubmitTicketAsync` mints the ticket id, checks the player floors locally, and returns a
  `TicketHandle` that polls every 2 s (`Retry-After`, 404 expired, five failures in a row failed, stop
  at `expiresAt` plus 10 s), cancels (200; 409 reads the match; 404), and builds the match or backfill
  join ticket (`CreateJoinTicket`).
- Wire DTOs `ReserveRequest`, `QuickJoinRequest` with `QuickJoinFilters`, `ReservationRecord`, beside
  the list, location, token, reservation and ticket DTOs.

### Heartbeat tier (`PingCore.Discovery.Host`)

- `HeartbeatReporter.Create(HeartbeatReporterOptions)` with `StartAsync` (`Started`, `Refused` with the
  409 `self_hosted_cap` or `ip_cap` reason and `limit`, `LocalSdkEndpointPresent` when
  `AGONES_SDK_HTTP_PORT` names a port, or `Failed`): a beat every 30 s with up to 3 s jitter against the
  90 s TTL, `Retry-After` after a 429, retries at 5, 10 and 20 s after a 503 or no answer, a warning once
  two beats in a row are missed, and a stop after a 409. `SetPlayers` and `SetMeta` send a change early,
  at most once per 5 s; `Beat` and `Status` (`VerificationMode`, `Verified`, `LastProbeError`,
  `ExpiresIn`, `MissedBeats`); `StopAsync` delists (`DELETE /v1/servers/{serverId}`, the id encoded). The
  token is a runtime option only and never logged; a token without the `dsc_` prefix is refused before
  sending.
- `VerifyReservationAsync(reservationId, playerId)` always sends the own `serverId` and the
  `playerId`; the verdict-only answer maps to `Valid` or `Invalid` (`Detailed` false), a detailed answer
  is `Valid` only for this game server and a listed player (`WrongServer`, `NotInReservation`), and 429,
  503 or no answer is `Unavailable`, never valid.
- `UdpEchoResponder.Bind(port)` (and `TryBind`) answers Discovery's `udp-echo` probe: exactly the
  21-byte DSCV1 challenge, echoed byte for byte from the same socket, every other datagram dropped, at
  most 5 replies per source per 10 s.
- Wire DTO `DelistResponse`, beside the heartbeat and verify DTOs.

### Local SDK shim (`PingCore.Fleet`)

- `FleetSdk.Create` and `IFleetSdk` with `StartAsync` (GET-only start, then the watch stream),
  `ReadyAsync` (then health pings every 2 s), `SetCounterAsync` and `GetCounterAsync`,
  `EndSessionAsync`, `PublishJoinableAsync` and `WithdrawJoinableAsync`, `GetBackfillsAsync`,
  `GetReservationAsync` (waits up to 2 s for a trailing push) and `ListReservationsAsync`,
  `ShutdownAsync` and `NotifyProcessStopping`; the `FleetState` lifecycle with `StateChanged`,
  `GameServerChanged`, `AllocationReceived` (once per allocation id) and `AllocationCleared` (ended by
  the game or cleared by the platform). Inert, with no I/O, when `AGONES_SDK_HTTP_PORT` is unset or not
  a port. No call throws for an HTTP or transport failure; each returns a `FleetCallOutcome`.
- Its own loopback `HttpClient` transport (the watch stream is read line by line as it arrives) and an
  `Awaitable` scheduler, so results and events arrive on the Unity main thread; `FleetSdkOptions`
  replaces either.
- Stop handling: on a container stop the supervisor refuses new connections while the watch stream stays
  open until SIGTERM, so a call refused after the endpoint has answered once is `EndpointClosed`
  (logged at info); a timeout, or an endpoint that never answered, is `Unreachable`. The five seconds
  after the watch closes still count as closed. A failed `EndSessionAsync` keeps the allocation's ending
  mark unless the endpoint refused the call (`Rejected` or `Unsupported`), so a clearing frame after a
  lost answer is `EndedByGame`.
- `SetCounterAsync` documents the counter rule: write `players` only, never `sessions`, whose count the
  supervisor tracks itself and the matchmaker allocates against.
- Session helpers (`PingCore.Fleet.Sessions`): `MatchContext.Parse` and `BackfillContext.Parse` (the
  matchmaker's allocation and backfill contexts, typed; `MatchContext.HasRoster` tells a rosterless
  allocation apart), `BackfillWatcher` (reads `GET /v1/backfills` on a new backfill annotation, raises
  each backfill once, and waits for one a join outran), `HostedAdmissionEvidence` (the pushed hold, the
  current allocation's roster or player cap, the delivered backfill after up to 5 s), and
  `JoinableSessionKeeper` with the pure `JoinablePlan` (open seats clamped to the `players` counter's
  free capacity, republished on change and every `ttl / 2`, withdrawn at 0 and on stop). A 2xx publish
  is local acceptance only; a refusal by Discovery never reaches the game.
- Wire DTOs `CounterPatchRequest`, `JoinableSessionRequest`, `JoinableSessionRecord`, `BackfillList`,
  `BackfillView`, `WatchFrame`, `LocalSdkMessage`; `CounterView` also models the PATCH echo.

### Connection approval (`PingCore.Netcode.NGO`)

- `PingCoreConnectionApproval`: `Install` turns on `ConnectionApproval`, raises
  `ClientConnectionBufferTimeout` to at least 15 s, answers every connection `Pending` and decides it on a
  later main-thread step, approves a listen host's own client at once, gives seats back on disconnect,
  and raises `Decided` with the ticket ref only.
- `HeartbeatAdmissionEvidence`: verify through the game server's `HeartbeatReporter`; a detailed answer's
  seats are counted like a hosted hold's, a verdict-only answer admits once per player.

### Settings

- `PingCoreClientSettings` asset (the `dscp_` public ids, and the open app's heartbeat token, which the build
  guard rejects until its scope can be confirmed). `DiscoveryBaseUrl` is the constant
  `https://discovery.pingcore.io` (`DefaultDiscoveryBaseUrl`) and no longer a serialized field: an older asset's
  `discoveryBaseUrl` is ignored. Only a project compiled with the `PINGCORE_DISCOVERY_URL_OVERRIDE` scripting define
  gets an override field, which takes over that old value through `FormerlySerializedAs`; the define is reserved
  for testing against another Discovery host, and nothing sets it today.
  `LoadFromResources()` loads the asset by `ResourcesName` when it sits in a `Resources/` folder, where the Editor
  plugin creates it.

### Hardening

- **A supervisor self-allocation is never rosterless by default.** Its id is `self-<ms>`, the supervisor's
  clock, so a `match` join ticket naming it is `not_in_roster` unless the game sets the new
  `ApprovalOptions.AllowSelfAllocatedJoins` (local testing, or a game that self-allocates and accepts a
  guessable id). A backend allocation without a roster is still admitted on its id alone; the backend must
  mint an unguessable `allocationId` or let Discovery mint one. `AdmissionFacts` gains `SelfAllocation` and
  `AllowSelfAllocatedJoins`.
- **An unreadable allocation context fails closed.** `AllocationInfo.ContextInvalid` (and
  `MatchContext.ContextInvalid`) marks a context annotation that did not parse; `MatchContext.HasRoster` is
  then true, so a `match` join is `not_in_roster` instead of being read as rosterless.
- **No resend under a new player id.** After a 401 re-issue that changes the player id (a re-issued
  anonymous token always does), a reservation read or release and a ticket poll or cancel are not resent:
  they answer `Unauthorized` with the new `DiscoveryCallResult.IdentityChanged`, and a `TicketHandle` ends
  `Failed` with that result. The cache raises the new `PlayerTokenChange.IdentityChanged`
  (`PlayerTokenEvent.PreviousPlayerId`) whenever a new token names another player id. Reserve, quick join
  and submit are resent under the new player only after a 401 on their first attempt: once an earlier
  attempt of the same call may have reached Discovery (a 503, an unexpected answer or no answer), they are
  not resent either and answer `Unauthorized` with `IdentityChanged`, because Discovery scopes their
  reservation id, idempotency key and ticket id per player and the old hold or ticket may already exist.
- **A ticket poll 429 without a readable `Retry-After`** (WebGL hides it unless Discovery exposes it) backs
  off through `RetryGovernor` and counts as a failure, instead of polling every 2 s for ever.
- **Transport messages are scrubbed.** `DiscoveryCaller` replaces URLs and paths in a
  `PingCoreTransportException` message (which can quote the poll URL, ticket id included) with `<url>`,
  keeps the exception type and the engine's phrase, and caps it at 200 characters
  (`DiscoveryCaller.ScrubTransportMessage`).
- **One random source.** The heartbeat jitter draws from `SecureIds.NextUnit` (`RandomNumberGenerator`);
  a source scan bans `System.Random` in the runtime.
- **`queryPort` can be left out.** `HeartbeatReporterOptions.OmitQueryPort` sends none, so a `tcp` app's
  probe dials the game port (`SentQueryPort`); the reporter warns once when Discovery answers `tcp` while a
  `queryPort` is sent.
- **Backfill reads are single-flight.** Concurrent `BackfillWatcher.WaitForAsync` calls and the annotation
  refresh share one `GET /v1/backfills` per poll tick; a waiter's cancellation never cancels the shared read.
- **One port parse.** `PingCore.Core.LocalSdkPort.TryParse` is the `AGONES_SDK_HTTP_PORT` parse that the
  local SDK shim and the heartbeat tier both use.
- Files split for size, no API change: `PlayerTokenCache` (`.Acquire`), `TicketHandle` (`.Polling`, with
  `TicketState` and `MatchAssignment` in `TicketTypes.cs`), `ServerListQuery` (`MetaOp`, `ServerSort` in
  `ServerListTypes.cs`, `LatencyKeys` under `Internal/`), `HeartbeatReporter` (`.Stop`, `.Validation`,
  `.Events`) and `JoinableSessionKeeper` (`.Loop`).

### Fixes

- **Latency medians no longer snap to the frame time.** At 30 fps every median was a multiple of 33 ms,
  because the default transport's receive resumed on the Unity main thread before the pong was stamped.
  `IWebSocketEchoSession.ReceiveTextAsync` now returns an `EchoMessage` carrying the `Stopwatch` timestamp
  read on the thread that completed the socket read, and the probe starts each receive before it sends.
  A loopback test with a 7 ms beacon and a 30 fps main thread pins it.
- **`GetReservationAsync` looks again for the whole `ReservationWait`.** The last wait is cut short so the
  final lookup lands at the deadline; before, it gave up up to one poll early (1.8 s live, not 2 s).
- **The sample's client build is hardened** (sample code, no SDK change): `BeaconRush.Editor.ClientBuild`
  refuses an editor not launched on StandaloneWindows64 with the Player subtarget, and fails on any
  missing script in `Client.unity`, after a client built from an editor left on the Dedicated Server
  subtarget shipped without its client components.

### WebGL latency

- **The latency probe works in WebGL players.** `LatencyProbe.IsSupported` is now true everywhere: a WebGL player,
  which has no `ClientWebSocket`, reaches each beacon through the browser's WebSocket
  (`Runtime/Discovery/Latency/WebGL/PingCoreLatency.jslib`, a WebGL-only plug-in, with `WebGlEchoTransport`
  compiled only for WebGL players), picked as the default transport there. The plug-in passes each message's
  age since the browser created its message event (`event.timeStamp`), so the time a pong waited in the browser's
  queue after that, behind a Unity frame for example, is taken off the round trip; a wait before the browser
  created the event (its main thread busy when the pong came in) stays in it. The probe's procedure (warm-up, median of 5, a failed location left out, never 0) is the same
  code on every platform. IL2CPP and WebGL builds are not yet verified.
- On WebGL the SDK cannot read Discovery's `Retry-After` header (Discovery does not expose it to cross-origin
  pages), so after a 429 the Discovery calls wait their fixed backoff instead of the time Discovery asked for.

### The solo join

- **`IFleetSdk.AllocateSelfAsync`**: `POST /allocate` on the local SDK endpoint, a self-allocation
  (`self-<ms>`, empty context) that puts the game server `in_session` so the matchmaker skips it. Confirmed by the
  watch frame (`SelfAllocationResult.Allocation`, `IsConfirmed`, within `FleetSdkOptions.SelfAllocationWait`,
  2 s); concurrent calls share one request; never sent while an allocation is current (`AlreadyAllocated`),
  because the supervisor would replace that allocation, nor again after a request whose answer was lost or whose
  frame did not come, until a frame arrives or `FleetSdkOptions.SelfAllocationGrace` (10 s) passes
  (`AwaitingEarlier`). The shim logs a warning when a platform allocation is replaced by the self-allocation, and
  when one replaces the self-allocation.
- **The session claim, opt-in** (`ApprovalOptions.ClaimIdleSessions`, default false; `PingCore.Core.Handshake`): the
  hosted `reservation` cell takes a hold on an idle game server too, the game's gate decides whether idle is allowed,
  and with the opt-in `AdmissionPipeline` approves such a join only after the evidence claimed a session
  (`ISessionClaimEvidence`, `JoinAdmission.NeedsSessionClaim`, `SessionClaimOutcome`, `AdmissionDecision.SessionClaim`).
  A platform allocation that came first asks the gate again (`SessionClaimOutcome.AllocatedMeanwhile`); a failed claim
  is `refused_by_game`, one out of time `approval_timeout`, one during which the game server began stopping
  `stopping`; the seat is given back. The joiner is admitted on the hold; a self-allocation's id still admits no
  `match` join unless `ApprovalOptions.AllowSelfAllocatedJoins`. `HostedAdmissionEvidence` implements the claim with
  `AllocateSelfAsync`; an opted-in hosted evidence that cannot claim refuses such a join. A game that opts in must end
  every claimed session itself, one nobody joined included. Without the opt-in an idle reservation is decided as
  before.

### Quickstart sample

- `Samples~/Quickstart`: one scene, `Quickstart.unity`, that plays Beacon Rush against the developer's own Beacon
  Rush fleet. It carries a copy of Beacon Rush's client code, prefabs and materials under `BeaconRush/` with their
  original GUIDs (Netcode for GameObjects only lets a client join a game server whose network prefabs match its
  own), its own `QuickstartClientSettings.asset` (the fleet and community apps' public ids, shipped empty: the
  Editor plugin's Connect writes the developer's own; the heartbeat token empty) and `QuickstartBootstrap`, which adds **Quick play** (the solo join below: a seat
  on a fleet game server with room, an idle one claiming itself first; with no seat anywhere it falls back to a
  matchmaking ticket in the `rush-p2` queue, where a match starts once two players are queued, and the screen
  says so) and **Play on this PC** (a LAN-only game on this PC, joined from a second copy by Direct connect) to
  the copied menu. A second player in the same Editor comes from Unity's Multiplayer Play Mode: the copied client
  gives each additional Editor instance its own player profile from its player tag. Its assemblies
  compile only with Netcode for GameObjects 2.x installed; the README's first step installs 2.11.2. Never import
  it into a project that already holds Beacon Rush: the copies share its GUIDs and assembly names.

### What is missing

- `InfrastructureCheck`, `InfrastructureReport` and `InfrastructureState` (`Runtime/Discovery/Infrastructure/`): from
  the app id and one public server list call, the client says which part of its PingCore backend is missing (no app
  id, an app Discovery does not know or that is disabled, no game server listed, Discovery unreachable), with the
  exact messages a game shows; any other answer is a Discovery error, never "no game servers". Classified by status
  and `reason`, never message text.
- `InfrastructureEditorHook` and `InfrastructureQuestion`, compiled only with `UNITY_EDITOR`: the Editor plugin
  answers with the exact reason from the workspace (`InfrastructureReport.EditorDetail`), within 5 s on the game's
  scheduler; an answer that throws, comes late or looks like a credential is dropped.
- The Quickstart client shows the check as a banner at launch and after a failed join, and its settings asset ships
  with empty app ids.

### Contracts and tests

- Wire DTOs for the Discovery 1.5.1 and local SDK endpoint 1.3.4 contract fixtures, with fixtures for
  every new request, answer and error the SDK branches on.
- Handshake examples `reservation.party.json` and `invalid/oversize.json` (the folder holds payloads every
  decoder must refuse).
- EditMode tests: fixture round trip, fixture coverage, DTO dump, secrets-name scan, Core async bans;
  the handshake (`Tests/Editor/Handshake/`: the codec on every example, the reject table, the decision
  table cell by cell, the ledger, the pipeline on a manual clock); the Discovery client against a scripted
  fake Discovery (`FakeDiscoveryTransport`) on a manual virtual clock (`TestScheduler`), with the latency
  reference answers of the Discovery latency procedure (assembly `PingCore.Discovery.Client.Tests.Editor`); the
  heartbeat tier's schedule table, reporter, verify verdict table, local SDK endpoint check and echo
  responder over a real loopback socket (assembly `PingCore.Discovery.Host.Tests.Editor`); the Fleet state
  machine, policies, whole-shim and session-helper tests against an in-process fake local SDK endpoint,
  and strict round trips of the local SDK endpoint bodies its spec gives no schema
  (`Tests/Editor/Fleet/Bodies/`, format `pingcore-body/1`; assembly `PingCore.Fleet.Tests.Editor`); and the
  NGO approval driven through `ConnectionApprovalCallback` on a real `NetworkManager`.

[Unreleased]: https://github.com/pingcoregaming/pingcore-unity/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/pingcoregaming/pingcore-unity/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/pingcoregaming/pingcore-unity/releases/tag/v0.1.0
