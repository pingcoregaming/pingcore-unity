# Local SDK endpoint surface (supervisor 1.3.4)

The readable route table for the local SDK endpoint, which the supervisor (PID 1 in the game server's own container) serves on `127.0.0.1:$AGONES_SDK_HTTP_PORT`. The machine-readable contract is the snapshot `contracts/supervisor/sdk-openapi.json` (`info.version` 1.3.4). This table has exactly one row per snapshot operation, with the same method and path set as the snapshot.

The list action path `/v1beta1/lists/{name}:{action}` is a single path segment on the wire: the list name and the action are split at the colon.

Kinds:

- `agones`: a stock Agones SDK route that the supervisor actions.
- `agones-beta`: an Agones beta Lists route. No list can be declared, so it always answers 404.
- `pingcore`: a PingCore extension (session end, joinable sessions, backfills, Discovery reservations).
- `compat-noop`: accepted for Agones compatibility and does nothing (alpha player tracking, `sdk.Reserve()`).

"Not used" means `PingCore.Fleet` never calls the route in v1.

| Method | Path | Kind | SDK use |
|---|---|---|---|
| `POST` | `/ready` | agones | `PingCore.Fleet` Ready, once the game server can take players |
| `POST` | `/health` | agones | `PingCore.Fleet` health ping |
| `POST` | `/shutdown` | agones | `PingCore.Fleet` Shutdown (drain this game server) |
| `POST` | `/allocate` | agones | `PingCore.Fleet` `AllocateSelfAsync` (opt-in via `ApprovalOptions.ClaimIdleSessions`): claims an idle game server for a reservation joiner with an empty body; the game must end the resulting session itself |
| `GET` | `/gameserver` | agones | `PingCore.Fleet` reads the GameServer view (`PingCore.Fleet.Wire.GameServerView`, fixture `gameserver.json`) |
| `GET` | `/watch/gameserver` | agones | `PingCore.Fleet` watch stream parser: NDJSON `{result: GameServer}`, allocation and backfill annotations |
| `GET` | `/v1beta1/counters/{name}` | agones | `PingCore.Fleet` counter read (`PingCore.Fleet.Wire.CounterView`, fixture `counter.json`) |
| `PATCH` | `/v1beta1/counters/{name}` | agones | `PingCore.Fleet` `SetCounterAsync` with `{count}` (`PingCore.Fleet.Wire.CounterPatchRequest`, fixtures `counter.patch.request.json` and `counter.patch.json`). The game writes `players` only: an SDK-set `sessions` count beats the supervisor's own session tracking |
| `POST` | `/v1/sessions/{allocationId}/ended` | pingcore | `PingCore.Fleet` `EndSessionAsync` (a session or a backfill) |
| `POST` | `/v1/sessions/{allocationId}/joinable` | pingcore | `PingCore.Fleet` joinable-session publish (open seats for backfill) |
| `DELETE` | `/v1/sessions/{allocationId}/joinable` | pingcore | `PingCore.Fleet` joinable-session withdraw |
| `GET` | `/v1/backfills` | pingcore | `PingCore.Fleet` `GetBackfillsAsync` (`PingCore.Fleet.Wire.BackfillList`, fixture `backfills.json`) |
| `GET` | `/pingcore/reservations` | pingcore | `PingCore.Fleet` `ListReservationsAsync` (`PingCore.Fleet.Wire.LocalReservationList`, fixture `reservations.list.json`) |
| `GET` | `/pingcore/reservations/{reservationId}` | pingcore | `PingCore.Fleet` `GetReservationAsync`, the hosted reservation admit; 404 means unknown, released or expired (`PingCore.Fleet.Wire.LocalReservation`, fixture `reservation.json`) |
| `PUT` | `/metadata/label` | agones | Not used |
| `PUT` | `/metadata/annotation` | agones | Not used |
| `POST` | `/reserve` | compat-noop | Not used |
| `PUT` | `/alpha/player/capacity` | compat-noop | Not used |
| `GET` | `/alpha/player/capacity` | compat-noop | Not used |
| `POST` | `/alpha/player/connect` | compat-noop | Not used |
| `POST` | `/alpha/player/disconnect` | compat-noop | Not used |
| `GET` | `/alpha/player/count` | compat-noop | Not used |
| `GET` | `/alpha/player/connected` | compat-noop | Not used |
| `GET` | `/alpha/player/connected/{playerId}` | compat-noop | Not used |
| `GET` | `/v1beta1/lists/{name}` | agones-beta | Not used |
| `PATCH` | `/v1beta1/lists/{name}` | agones-beta | Not used |
| `POST` | `/v1beta1/lists/{name}:{action}` | agones-beta | Not used |
