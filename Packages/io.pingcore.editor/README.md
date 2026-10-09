# PingCore Editor (`io.pingcore.editor`)

The Editor plugin for games on PingCore. **Window > PingCore** signs in to your workspace, picks the fleet your game runs on, builds your Linux dedicated game server from a Build Profile, pushes it with the bundled `pingctl`, releases it onto the fleet and shows what the fleet runs. Its build guard fails any player build that contains a credential. Nothing in this package ships in a build: its assemblies are restricted to the Editor.

The full documentation is at [pingcore.io/docs/unity/unity-overview](https://pingcore.io/docs/unity/unity-overview).

## Install

Install the runtime SDK, `io.pingcore.sdk`, first: Unity finds this package's dependency on it only when your project lists it too. Then, in the Package Manager, choose **Install package from git URL** and paste:

<!-- pingcore:install:begin -->
`https://github.com/pingcoregaming/pingcore-unity.git?path=/Packages/io.pingcore.editor`
<!-- pingcore:install:end -->

Add `#v0.1.1` to the end to pin the release. Use the same release for both packages.

## Requirements

- Unity 6 (6000.0 or newer) with the Linux Dedicated Server Build Support module, added in Unity Hub.
- `io.pingcore.sdk` of the same version, and `com.unity.nuget.newtonsoft-json`.
- A PingCore workspace, with your game and a fleet set up in its panel. The plugin creates nothing in the workspace except the CDN push token it needs.
- Windows keeps your API key and push tokens in Credential Manager. On macOS and Linux this version keeps them in `EditorPrefs`, outside the project but not encrypted, and says so.
- The bundled `pingctl` covers Windows x64, macOS (Apple silicon and Intel) and Linux x64. Elsewhere, set your own under **Your own pingctl**.

## Documentation

- [The PingCore Window](https://pingcore.io/docs/unity/the-pingcore-window): Connect, Ship, Status and Player hosting, and the same build and push from the command line.
- [Keeping Secrets Out of Your Build](https://pingcore.io/docs/unity/keeping-secrets-out-of-your-build): the build guard, Confirm for build, and where the plugin keeps your credentials.
- [Run Beacon Rush on PingCore](https://pingcore.io/docs/unity/beacon-rush): the plugin used from start to finish.
- [Troubleshooting](https://pingcore.io/docs/unity/unity-troubleshooting): every message the window shows.
