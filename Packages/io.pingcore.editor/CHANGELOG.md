# Changelog

All notable changes to `io.pingcore.editor` are recorded here, in the form of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/). Both PingCore packages share one version and one tag.

<!-- Convention: the version being prepared is "## [X.Y.Z] - Unreleased"; the release is not
published until it reads "## [X.Y.Z] - YYYY-MM-DD". After a release, changes for the next one go under
a new "## [Unreleased]" heading at the top. Every heading has a link reference at the end of this file:
[Unreleased] compares the newest tag with HEAD, [X.Y.Z] points at the release tag. -->

## [0.1.1] - 2026-10-09

### Changed

- The documentation moved to pingcore.io (https://pingcore.io/docs/unity/unity-overview), which is now its single source. This package's README keeps what the package is, how to install it and links to those pages. No code changes.

## [0.1.0] - 2026-10-09

- Package scaffold: the `PingCore.Editor` assembly (Editor platform only) and its EditMode tests.
- Build guard rules: a project names the instrumentation it keeps out of plain builds in
  `ProjectSettings/PingCoreBuildGuardRules.json` (`restrictedAssemblyPrefixes`, `instrumentationDefines` and an
  optional `instrumentedOutputFolder`); a restricted assembly or an instrumentation define in a build that does not
  write inside that folder fails with `instrumentation_in_plain_build`, and a malformed rules file fails every build
  with `scan_error` naming the file and each problem (unknown or duplicate key, a key that must be a list of
  strings, trailing content, invalid JSON with its line and position), quoting nothing from the file but a key
  name. Without the file the guard has no such rules.
- Build guard: a preprocessor that fails early and an authoritative postprocessor that scans the
  build output. Typed reasons `editor_assembly`, `instrumentation_in_plain_build` and `secret_literal`;
  the verdict is written to `Library/pingcore-build-guard.json`.
- Build guard hardening: the output scan reads and deletes only what the build produced (an
  unrelated file beside the build is never touched); a source stage scans the source of every
  packed asset, the build scenes and their dependencies and every `Resources/` and
  `StreamingAssets/` folder; an output that cannot be byte-scanned passes only on that source
  scan (`outputScan: "unavailable"`) or fails with the new `output_unscannable` reason; files are
  streamed in chunks, so size no longer matters; a configured `openRegistrationHeartbeatToken`
  fails as `secret_literal` until a confirmation with its SHA-256 digest is recorded in
  `ProjectSettings/PingCoreBuildGuard.json` (written by the Editor plugin's Confirm for build).
- Build guard closing of gaps: any check that cannot finish fails closed with the new `scan_error`
  reason (the output is deleted and the verdict names the error's type only); produced entries are
  matched by exact name against a closed list, so `Game.env`, `Game_secrets.txt` or a sibling
  `Game/` folder beside the build is never read or deleted; an archive output (`.apk`, `.aab`,
  compressed data, Xcode) now also byte-scans the compiled player assemblies from the build's
  staging and passes only as `outputScan: "assemblies-only"`, failing with `output_unscannable`
  when none is found; findings in package files name `Packages/<name>/...`.
- `PingCore.Editor.Cli.BuildServer`: a headless Linux Dedicated Server (Mono) build,
  `-executeMethod PingCore.Editor.Cli.BuildServer.Run` with `-pingcoreVersion`, `-pingcoreScene`,
  `-pingcoreProduct`, `-pingcoreDefine` (repeatable) and `-pingcoreOutputRoot`; output under `Builds/Server/<v>/`
  (or `<output root>/<v>/`) with `version.txt` beside the executable. The server scripting
  backend is set to Mono for the build and restored afterwards. Exit codes: `0` when the build
  succeeded and the build guard's verdict is `pass`, `1` when the build or the guard failed, `2`
  for bad arguments, a missing scene or a missing Linux Dedicated Server module. The arguments
  are parsed by the pure `BuildServerArgs.Parse`, covered by EditMode tests.
- The Beacon Rush Windows client build (IL2CPP by default) is sample code,
  `BeaconRush.Editor.ClientBuild` in `SampleGame/Assets/Editor/BeaconRush/`, not part of this package;
  it reads this package's build guard verdict.
- `PingCore.Editor.Cli.MissingScriptScan`: counts the components of build scenes, and of
  the prefabs they use, whose script is gone or whose class this editor did not compile (an editor on
  the other subtarget). `Cli.BuildServer` runs it before building and exits `2` naming the scenes and
  objects. The sample's client build now refuses an editor not launched with
  `-buildTarget Win64 -standaloneBuildSubtarget Player` and runs the same scan, after a client built
  from an editor left on the Dedicated Server subtarget shipped its scene with missing scripts.
- `PingCore.Editor.Cli.BuildHousekeeping.Finish` returns `bool`: false when a settings
  file still differs after its restore or a performance test framework leftover is still on disk.
  `Cli.BuildServer` then exits `1` (`BuildHousekeeping.NotRestoredProblem`), and it runs `Finish` in a
  `finally` of its own, so a throw while restoring the backend cannot skip it. `MissingScriptScan`
  unloads a scene the hierarchy held unloaded again instead of removing it.
- Workspace foundation: the `PingCore.Editor.Workspace` assembly (Editor only; references
  `PingCore.Editor`, `PingCore.Core` and `PingCore.Discovery.Client`), and the package now depends on
  `io.pingcore.sdk` and `com.unity.nuget.newtonsoft-json`.
- Workspace credentials: the `usr_` key and the CDN push token live in Windows Credential Manager
  (`CredWriteW`/`CredReadW`/`CredDeleteW`, generic credentials named `PingCore/<host>/usr` and
  `.../cdnpush/<sourceId>`; secret buffers zeroed after use). Where that store is unavailable, and on macOS and
  Linux in this version, they fall back to `EditorPrefs` and say so; "This session only" keeps the key in
  `SessionState` (push tokens always stay in the persistent store). Sign-in verifies the key with `GET fleets`
  before storing it and shows only `usr_` plus its last four characters. Sign out deletes the key from both the
  persistent store and this session's; signing in to a different workspace deletes the previous workspace's key
  once the new key is verified and stored; every sign-in probes the OS credential store again.
- Workspace API client: every PingCore API call the plugin makes (fleets, fleet detail and live state, build
  targets, releases, the push token issue, the deployment, deployment specs and template set the startup command
  is read from, Discovery apps, capabilities), https only, redirects never followed, a success only for a 2xx JSON
  envelope with `error: false` (an HTTP 200 with `error: true` is a failure), typed errors (`NotSignedIn`,
  `MissingBrandPermission` naming the route's brand permission, `RateLimited` with `Retry-After`, and others).
  An issued push token goes straight into the credential store and is never returned. Wire DTOs are pinned by
  recorded API fixtures and a snapshot of the fleet routes' OpenAPI schemas, neither of which ships.
- Workspace child processes and redaction: children get an allowlisted environment only and are refused
  a token-shaped argument; every output line, API message and error is redacted (known secrets, prefixed
  tokens, 64-hex runs, game server keys). A child's output is drained asynchronously for at most 5 s after it
  exits, so a process it started that keeps the pipe open cannot freeze the Editor while reloads are locked.
  Project settings (`ProjectSettings/PingCoreEditor.json`) and per-developer settings
  (`UserSettings/PingCoreEditorUser.json`) hold ids and paths only and refuse a credential-shaped value by the
  command line's rule (a token prefix and eight or more letters or digits).
- Build guard confirmation writer: Confirm for build (Window > PingCore, Player hosting) checks the configured
  `openRegistrationHeartbeatToken` (a `dsc_` token, none of the plugin's own credentials), finds the app
  by its `dscp_` public id (`GET discovery/apps`), and requires it to be enabled and open with exactly one
  active heartbeat token ending in the token's last four characters and no active token of another scope
  ending in them (`GET discovery/apps/{id}`); then it writes the SHA-256 record to
  `ProjectSettings/PingCoreBuildGuard.json`, replacing an older record for the same app. The API shows
  tokens only as their last four characters, so the confirmation binds by app, open mode, scope and those
  four characters; the record's full digest still fails a build carrying any other token. The writer and the
  guard both digest the token with surrounding whitespace removed. The heartbeat token field is write-only: it
  never holds the asset's value, empties as soon as a typed token is applied, and a label says only whether a
  token is set (Clear removes it). The guard's unconfirmed-token finding says "confirm it on Window > PingCore,
  Player hosting".
- `PingCore.Editor.Build.ServerBuilder`: the body of `Cli.BuildServer`, callable in-process, so the command
  line and the Editor build through one code path (housekeeping, missing-script scan, guard verdict). With a
  Build Profile it builds through `BuildPipeline.BuildPlayer(BuildPlayerWithProfileOptions)` with the profile's
  own scenes, backend and defines; the scene form sets the Mono backend and restores it. An in-Editor build is
  always plain (no extra define), refuses Play mode and a compile in progress, and switches the Editor
  back to its active build target afterwards: a Linux Dedicated Server build from an Editor on another target
  leaves the Editor on Linux / Server (seen on 6000.4.10f1), which would compile every `!UNITY_SERVER` assembly
  out of it at the next domain reload. The command line never switches back, as before.
- Pipeline (`PingCore.Editor.Workspace.Pipeline`): a pure planner for build, push through `pingctl`, release
  and watch, with the release rules proved live against PingCore: the release targets the snapshot `pingctl` printed,
  the build targets must list it (polled every 5 s for up to 90 s), the version the fleet is already pinned to
  is not released again unless forced, a release is never retried except once on `server_unreachable`, a
  failed release is acknowledged. The run state is saved to `UserSettings/PingCoreDeployState.json` after every
  step (ids and versions only; a corrupt file is set aside); a saved run never continues by itself, the
  Release row's Continue does it, and a continue restarts the build-targets lag clock. Assembly reloads are
  locked while `pingctl` runs.
- The push: `pingctl version` (0.1.0 or newer) then `pingctl push` with Unity's two do-not-ship folders
  excluded; the push token and the workspace API base reach `pingctl` through its environment only, never
  an argument, and every output line is redacted.
- Tests that need the API contract snapshots (not published) are ignored with the reason where they are absent,
  instead of failing.
- `PingCore.Editor.Cli.SceneScanCli`: a headless missing-script check of named scenes,
  `-executeMethod PingCore.Editor.Cli.SceneScanCli.Run -pingcoreScene <Assets/.../Scene.unity>` (repeatable),
  running each scene and the prefabs it depends on through `MissingScriptScan`. It logs one
  `[PingCore SceneScan]` line, `PASS` with the counts or `FAIL` naming the objects. Exit codes: `0` no missing
  script; `1` a missing script or an exception; `2` no `-pingcoreScene`, a path that is not a `.unity` asset,
  or a scene the project does not have.
- No workspace URL: the plugin calls `https://app.pingcore.io/api`, and PingCore finds the workspace from the
  key. Sign-in verifies the key with `GET fleets`, then shows the workspace's name from `GET me/capabilities`.
  The key is kept per API host (`PingCore/app.pingcore.io/usr`). A development override,
  `apiBaseOverride` in `UserSettings/PingCoreEditorUser.json` (https only, never in the UI), moves every call
  and shows a warning banner; a malformed one stops every call instead of falling back.
- The plugin never reads the game list or a game's container list. Some reads are made key-only: only the
  fields the plugin needs are kept.
- One window, Connect, ship and report: Window > PingCore (the only menu item) is one page of four sections,
  **Connect**, **Ship**, **Status** and the **Player hosting** fold, each with its status and one next action.
  There is no Preferences or Project Settings page. Games, startup commands and fleets are set up in the panel
  or through MCP: the plugin creates nothing in the workspace but the CDN push token, never edits a fleet and
  never starts game servers. `ProjectSettings/PingCoreEditor.json` holds `gameId`, `fleetId`,
  `gameBranchId` (the branch Push pushes to) and `buildProfile`; other fields an earlier build wrote are dropped
  on the next save.
  - Connect: sign in with the `usr_` key, then pick a fleet; the fleet answers its game (whose branches Push
    resolves from), its Discovery app and its deployments. A fleet with no deployment says so with a link to the
    panel's deploy page, and still builds and pushes. Panel links use the workspace's own panel address from
    `GET me/capabilities` (`identity.brandUrl`), else `https://app.pingcore.io`. The fleet's Discovery app
    public id goes into the `PingCoreClientSettings` asset, which the plugin owns: it uses the project's own
    wherever it is, or creates `Assets/PingCore/Resources/PingCoreClientSettings.asset` at its first write.
  - Ship: three rows, Build, Push and Release, each idle, running, done or failed (with PingCore's words,
    Retry, which runs again what failed on that row, and Open in panel for Push and Release), one at a time.
    Build uses a Linux Dedicated Server **Build Profile** of the
    project and names the executable after the file the game's startup command launches; when the game launches
    several files, **Server executable** beside the profile picks one (kept as `processName` in
    `ProjectSettings/PingCoreEditor.json`), and when none is found the row says the product name is used. Push
    uploads to the CDN source of the fleet's game branch, never one taken from its deployments: **Push to branch** (the only branch
    without asking, else the developer's pick, remembered) and that branch's data source decide, with one
    sentence and Open branch in panel for a branch with no data source, an image, Steam or no CDN source, and
    "Pushes to CDN source #N" when it can. Push reads the game's startup command, never from a branch's default
    deployment spec: from the fleet's deployments (each one's spec, then its template set; the build must hold every
    file, each named when they differ), or, with no deployment, from the game's template sets (any one of their files
    passes, and the result names it). It refuses a build without them, and skips that check with a reason when no
    command-line config names a process. It issues the CDN push token only when none is
    stored for that source and nothing else would refuse the push, after a confirmation that issuing replaces the
    token for every other holder; **Use an existing push token** keeps a token issued elsewhere instead, after
    PingCore accepted it for that source. Release, the only step that needs deployments, is disabled with none,
    warns when they deliver from another CDN source than the push went to, refuses members on different sources,
    and releases the snapshot the push published, with Stop, Cancel release, Acknowledge and Continue.
  - Status: the fleet's live state and each deployment's location, count, status and build version, read only.
  - Player hosting: the fleet app's id, the community app with a Change dropdown of the workspace's open
    Discovery apps, the write-only community heartbeat token and Confirm for build.
- `pingctl` 0.1.1 is bundled under `Tools~/pingctl/<os>-<arch>/` with `manifest.json` pinning each binary's
  SHA-256; the plugin uses your own path, then `PINGCTL_BIN`, then the bundled binary (refused when it does not
  match the manifest), and never a `pingctl` found on `PATH`: a platform without a bundled binary is told to set
  its own. The push runs a private copy of the bundled binary under `Library/PingCore/pingctl-run/`, checked
  against the manifest through a handle held until the push ends and deleted afterwards. The manifest sits beside
  the binaries, so the check catches a corrupt or partial install, not a deliberate swap of the package's files.
- In the Editor, Beacon Rush's missing-infrastructure banner asks the plugin for the exact reason on a thread-pool
  thread, after the 5 s timeout has started; the plugin's `EditorPrefs` and `SessionState` reads go to the main
  thread, each bounded, so a slow credential store or file can no longer hold Play.
- `Cli.BuildServer.Run` takes `-buildProfile <Assets/...asset>`; the scene form (`-pingcoreScene`,
  `-pingcoreDefine`, `-pingcoreOutputRoot`) builds from a scene list, and mixing the two is an error. The default
  version is the UTC date and time, `yyyy.MM.dd-HHmmss`.
- A server build, from Ship or `Cli.BuildServer.Run`, puts the Editor's active build profile, subtarget and target
  back the way it found them, profile first, even when the build fails, is cancelled or throws: Unity's
  `BuildPlayer(BuildPlayerWithProfileOptions)` makes the profile it built the active one and keeps it across
  restarts, which left client scenes playing with no UI.
- Window > PingCore names an active Dedicated Server build profile or subtarget at the top of the page, with a button
  to Build Profiles, and Play mode logs one error naming the cause and the fix when it starts on one with missing
  scripts in the open scenes. The plugin never switches the profile itself.
- Connect is done once signed in with a fleet picked; a fleet with no deployment is shown under Status, with Add a
  deployment in the panel. A focus of the window reads the fleet again once its facts are a minute old.
- Push to branch names each branch as the panel does (`branchDescription`, for example "Main"), not by its legacy
  `branch-<id>` identifier.

[Unreleased]: https://github.com/pingcoregaming/pingcore-unity/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/pingcoregaming/pingcore-unity/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/pingcoregaming/pingcore-unity/releases/tag/v0.1.0
