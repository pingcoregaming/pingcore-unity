using System.Runtime.CompilerServices;

// The pure policies (heartbeat schedule, verify verdict mapping, DSCV1 frame and rate cap, the
// local SDK endpoint check) are internal and table-tested from the Host EditMode tests, which
// also inject the environment reader, the jitter source and the echo clock through internal members.
[assembly: InternalsVisibleTo("PingCore.Discovery.Host.Tests.Editor")]
