# Quickstart: Beacon Rush on PingCore

One scene that plays Beacon Rush, PingCore's sample game, against your own Beacon Rush fleet: **Quick play** and **Find match** find a game server through PingCore Discovery and join it over Netcode for GameObjects. **Play on this PC** hosts a LAN game with no PingCore service at all.

The sample carries a copy of Beacon Rush's client (`BeaconRush/`), because a client joins only a game server whose network prefabs and network code match its own. Leave `BeaconRush/` as it is.

## Requirements

- Unity 6 (6000.0 or newer), a project on the built-in render pipeline, and **Active Input Handling** set to Input Manager or Both.
- Netcode for GameObjects **2.11.2** exactly (Package Manager, Install package by name, `com.unity.netcode.gameobjects`): Beacon Rush game servers are built with it, and a client on another version cannot join.
- Both PingCore packages, `io.pingcore.sdk` and `io.pingcore.editor`.
- Never import this sample into a project that already holds Beacon Rush (the repository's `SampleGame`): the copies share its asset GUIDs and assembly names.

## Play

1. Open `Quickstart.unity` from this folder and press **Play**. The menu shows a banner saying what is missing: the sample ships with no app id.
2. Put Beacon Rush on your own fleet: follow [Run Beacon Rush on PingCore](https://pingcore.io/docs/unity/beacon-rush) in the repository's `SampleGame` project.
3. In this project, open **Window > PingCore**, sign in and pick the same fleet. Connect writes its app id into `QuickstartClientSettings.asset`.
4. Press **Play** again. **Find match** needs a second player; [Troubleshooting](https://pingcore.io/docs/unity/unity-troubleshooting#multiplayer-play-mode) shows how to start one from the same Editor.

If something does not work, see [Troubleshooting](https://pingcore.io/docs/unity/unity-troubleshooting).
