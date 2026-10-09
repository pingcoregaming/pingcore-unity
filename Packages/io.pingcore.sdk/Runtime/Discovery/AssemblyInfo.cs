using System.Runtime.CompilerServices;

// The pure policies (issuance gate, ticket floors, latency median, JWT claim reader) are internal
// and table-tested by the client's EditMode suite.
[assembly: InternalsVisibleTo("PingCore.Discovery.Client.Tests.Editor")]
