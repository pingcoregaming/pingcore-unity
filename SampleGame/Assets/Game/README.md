# Beacon Rush

PingCore's sample game: a top-down arena for 2 to 8 players on Netcode for GameObjects. Players steer, grab the glowing beacons, and the first to 10 wins. The same game runs on a PingCore fleet, as a self-hosted dedicated game server, and as a listen host inside a player's game, online or LAN only.

## Requirements

- Unity 6.3 or newer (built and tested on 6000.4.10f1) with the Linux Dedicated Server Build Support module, added in Unity Hub.
- Nothing to install: this `SampleGame` project references both PingCore packages from the repository by path, with Netcode for GameObjects 2.11.2 and Unity's Multiplayer Play Mode.
- A PingCore workspace with prepaid credit, to play on your own fleet.

## Play

Open this `SampleGame` folder as a project, open `Assets/Client/Scenes/Client.unity` and press **Play**. The project ships with no app id, so the menu shows a banner saying what is missing. [Run Beacon Rush on PingCore](https://pingcore.io/docs/unity/beacon-rush) takes it from there to two players in a match on your own game server. **Host a game**, **LAN only** plays on this PC with no PingCore service at all.

Never import the PingCore SDK's Quickstart sample into this project: it is a copy of this game with the same asset GUIDs and assembly names.

## Documentation

- [How Beacon Rush Works](https://pingcore.io/docs/unity/how-beacon-rush-works): where the game calls the SDK, its hosting modes and its admission gate.
- [Adding PingCore to Your Own Game](https://pingcore.io/docs/unity/adding-pingcore-to-your-game): what to copy from Beacon Rush, and what to leave.
- [Troubleshooting](https://pingcore.io/docs/unity/unity-troubleshooting).
