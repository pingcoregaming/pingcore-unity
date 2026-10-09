# PingCore SDK for Unity (`io.pingcore.sdk`)

The runtime SDK for games on PingCore. Game clients use it to find, reserve, match into and join game servers through PingCore Discovery. Game servers use it to report to their fleet, to list themselves when self-hosted or player-hosted, and to admit players through Netcode for GameObjects connection approval.

The full documentation is at [pingcore.io/docs/unity/unity-overview](https://pingcore.io/docs/unity/unity-overview).

## Install

In the Package Manager choose **Install package from git URL** and paste:

<!-- pingcore:install:begin -->
`https://github.com/pingcoregaming/pingcore-unity.git?path=/Packages/io.pingcore.sdk`
<!-- pingcore:install:end -->

Add `#v0.1.1` to the end to pin the release. Install the Editor plugin, `io.pingcore.editor`, the same way afterwards.

## Requirements

- Unity 6 (6000.0 or newer).
- `com.unity.nuget.newtonsoft-json`, which the Package Manager adds as a dependency.
- Netcode for GameObjects 2.x for connection approval. It is optional: `PingCore.Netcode.NGO` compiles only when `com.unity.netcode.gameobjects` 2.x is in the project.
- Verified with Mono builds. IL2CPP and WebGL builds are not yet verified.

## Example

A game client finds a seat on a game server with room:

```csharp
using PingCore.Discovery.Client;

DiscoveryClient client = DiscoveryClient.Create(settings, settings.FleetAppPublicId);
var seat = await client.QuickJoinAsync(new QuickJoinOptions(), ct);
if (seat.IsOk)
{
    Connect(seat.Value.Ip, seat.Value.Port.Value, seat.Value.ReservationId);
}
```

## Samples

**Quickstart** (Package Manager > PingCore SDK > Samples) plays Beacon Rush, the sample game, in one scene. Its README lists what it needs.

## Documentation

- [The Client SDK](https://pingcore.io/docs/unity/unity-client-sdk): player tokens, the server list, latency, quick join, reservations, matchmaking and joining.
- [The Game Server SDK](https://pingcore.io/docs/unity/unity-game-server-sdk): a dedicated game server on a PingCore fleet.
- [Self-Hosted Game Servers and Listen Hosts](https://pingcore.io/docs/unity/unity-self-hosted-and-listen-hosts).
- [Admitting Players](https://pingcore.io/docs/fleets/admitting-players): the join ticket, the decision and every reject reason.
- [Troubleshooting](https://pingcore.io/docs/unity/unity-troubleshooting).
