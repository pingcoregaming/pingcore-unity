using System.Runtime.CompilerServices;

// The pure policies of the workspace tooling (envelope classification, argument quoting, the
// Windows credential marshalling) are internal; their tables are tested from this assembly.
[assembly: InternalsVisibleTo("PingCore.Editor.Workspace.Tests")]
