using System.Runtime.CompilerServices;

// The game's EditMode tests reach the hosting modes and the approval event's fields, which are internal.
[assembly: InternalsVisibleTo("BeaconRush.Game.Tests.Editor")]
