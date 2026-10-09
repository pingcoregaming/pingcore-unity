# What the Quickstart copies from Beacon Rush

For the Quickstart sample (`Packages/io.pingcore.sdk/Samples~/Quickstart`). A client joins a live
Beacon Rush game server over Netcode for GameObjects only when it holds the same network prefabs (NGO derives each
prefab's `GlobalObjectIdHash` from the prefab asset's GUID), the same `NetworkBehaviour` types in the same order and
the same NGO version (2.11.2). So the Quickstart carries a copy of the files below **with their `.meta` files**.

**GUIDs must be preserved.** Copy every `.meta` byte for byte; never let Unity regenerate one. Never import the
Quickstart sample into `SampleGame` itself: the copies would collide with these GUIDs.

## Network prefabs (GUIDs and hashes fixed)

| File | GUID | `GlobalObjectIdHash` |
|---|---|---|
| `Assets/Game/Prefabs/Player.prefab` | `406aeaa5229def8448bcaa6fa0d9b217` | `1393086797` |
| `Assets/Game/Prefabs/Beacon.prefab` | `e0b4527fd30023349a04404ca059feb3` | `3048181459` |
| `Assets/Game/Prefabs/ScoreBoard.prefab` | `e16a36c489f9a614a9e654ca48c64c04` | `2432384827` |
| `Assets/DefaultNetworkPrefabs.asset` | `f011378a04d590f45a3a2cd8532a3a64` | the three above, in that order |

The prefabs gain visual children and plain `MonoBehaviour`s only (`PlayerLook` on the player, `BeaconLook` on the
beacon); no `NetworkBehaviour`, `NetworkVariable` or RPC is added, so protocol 2 and the hashes stand.

## Runtime game code: assembly `BeaconRush.Game`

Everything under `Assets/Game/` except `Tests/`, `Scenes/`, `README.md` and this file:

- `Assets/Game/BeaconRush.Game.asmdef`
- `Assets/Game/Admission/**`, `Assets/Game/Hosting/**`, `Assets/Game/Session/**`, `Assets/Game/Networking/**`
- `Assets/Game/Match/**` (now also `PlayerPalette.cs`, `PlayerLook.cs`, `BeaconLook.cs`, `PickupFlash.cs`)
- `Assets/Game/Prefabs/**` (the three prefabs above)
- `Assets/Game/Art/**` (the Standard-shader materials the prefabs and the arena use)
- `Assets/DefaultNetworkPrefabs.asset`

## Client views: assembly `BeaconRush.Client`

- `Assets/Client/BeaconRush.Client.asmdef`, `Assets/Client/ClientBootstrap.cs`, `Assets/Client/ClientLaunch.cs`
- `Assets/Client/Flows/**`, `Assets/Client/Models/**`
- `Assets/Client/UI/**`: the UI Toolkit client (`ClientUi*.cs`, `MatchView.cs`, `Views/*.cs`, `Layout/ClientMenu.uxml`,
  `Layout/BeaconRush.uss`, `Layout/BeaconRushTheme.tss`, `BeaconRushPanel.asset`)
- `Assets/Client/Settings/PingCoreClientSettings.asset` only if the Quickstart reuses it (its heartbeat token stays empty)

Not copied: `Assets/Client/Tests/**`, `Assets/Client/Scenes/Client.unity` (the Quickstart has its own scene),
`Assets/Editor/**` (the asset, scene and build scripts).

## Generated, not copied

- `Quickstart.unity` (with its `.meta`, a fixed GUID) is generated from `Assets/Client/Scenes/Client.unity` by a
  sync script that is not part of the public repository: the settings reference is pointed at the sample's own
  `QuickstartClientSettings.asset` and a `QuickstartBootstrap` component joins the client root. Never edit
  `Quickstart.unity` by hand.
- The two copied assembly definitions (`BeaconRush.Game`, `BeaconRush.Client`) are copied byte for byte like
  everything else, so they carry the Netcode for GameObjects 2.x gate here in SampleGame (`versionDefines`
  `PINGCORE_NGO` for `[2.0.0,3.0.0)` and the matching `defineConstraints` entry). The script refuses an asmdef
  without it rather than rewriting it, and its check mode fails on any drift.

## Dependencies the copy needs

`com.unity.netcode.gameobjects` 2.11.2, `com.unity.transport` (2.7.2 here), `com.unity.collections` (through NGO),
`com.unity.nuget.newtonsoft-json`, the built-in modules `com.unity.modules.uielements` and `com.unity.modules.imgui`,
the built-in render pipeline (Standard shader), and the legacy Input Manager axes `Horizontal` and `Vertical`.
No uGUI, no TextMeshPro, no Input System package.
