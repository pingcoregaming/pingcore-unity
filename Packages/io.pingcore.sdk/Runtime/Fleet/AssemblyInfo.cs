using System.Runtime.CompilerServices;

// The pure policies (state machine, line splitter, allocation tracker, reservation lookup
// policy, health cadence) are internal and table-tested from the Fleet EditMode tests.
[assembly: InternalsVisibleTo("PingCore.Fleet.Tests.Editor")]
