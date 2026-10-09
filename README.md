# PingCore for Unity

PingCore hosts and connects multiplayer game servers. This repository is the PingCore SDK for Unity: two Unity packages that let players find, reserve, match into and join your game servers, and Beacon Rush, a sample game you can play and fork.

**Documentation: [pingcore.io/docs/unity/unity-overview](https://pingcore.io/docs/unity/unity-overview).**

## What is in it

- [`Packages/io.pingcore.sdk`](Packages/io.pingcore.sdk/README.md): the runtime SDK, which ships in your game. Game clients find and join game servers; game servers report to their fleet and admit players through Netcode for GameObjects connection approval.
- [`Packages/io.pingcore.editor`](Packages/io.pingcore.editor/README.md): the Editor plugin, which never ships. **Window > PingCore** connects your project to a fleet and builds, pushes and releases your Linux dedicated game server.
- [`SampleGame`](SampleGame/Assets/Game/README.md): Beacon Rush, a top-down arena for up to 8 players on Netcode for GameObjects.
- [`contracts/handshake`](contracts/handshake): the join ticket's JSON schema with example payloads, for a game server on any engine ([Admitting Players](https://pingcore.io/docs/fleets/admitting-players)).

## Install

Install both packages from git, the SDK first. Each package's README has its git URL; add `#v0.1.1` to pin the release. [Adding PingCore to Your Own Game](https://pingcore.io/docs/unity/adding-pingcore-to-your-game) has the steps.

## Requirements

- Unity 6 (6000.0 or newer) with the Linux Dedicated Server Build Support module. Beacon Rush needs Unity 6.3 or newer.
- Netcode for GameObjects 2.x and Unity Transport 2.x.
- git on your `PATH`.
- A PingCore workspace ([Creating Your Account](https://pingcore.io/docs/getting-started/creating-your-account)).

## Start here

- [Run Beacon Rush on PingCore](https://pingcore.io/docs/unity/beacon-rush): the sample, from an empty workspace to two players in a match.
- [Adding PingCore to Your Own Game](https://pingcore.io/docs/unity/adding-pingcore-to-your-game): the same path with your project.
- [Troubleshooting](https://pingcore.io/docs/unity/unity-troubleshooting).

## Licence

Apache License 2.0: see [LICENSE](LICENSE) and [NOTICE](NOTICE).
