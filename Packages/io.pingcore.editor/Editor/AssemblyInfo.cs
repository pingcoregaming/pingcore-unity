using System.Runtime.CompilerServices;

// The build guard's confirmation writer and output deletion are internal: only the Editor plugin's
// own flows (and these tests) may call them. PingCore.Editor.Workspace holds the
// confirmation writer, which reaches BuildGuardConfirmations and BuildGuardConfirmationFile.
[assembly: InternalsVisibleTo("PingCore.Editor.Tests")]
[assembly: InternalsVisibleTo("PingCore.Editor.Workspace")]
