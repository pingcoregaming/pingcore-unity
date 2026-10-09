using System.Collections.Generic;
using UnityEngine;

namespace BeaconRush.Match
{
    /// <summary>
    /// The eight player colours and who gets which, pure and client-side only (nothing here is networked). A player's
    /// slot is its join order among the players on the score board: NGO hands out client ids in increasing order, so
    /// ranking the board's client ids gives the order they joined in, and every client that sees the same board picks
    /// the same colours. A player not on the board yet falls back to its client id modulo <see cref="Count"/>. When an
    /// earlier player leaves, the later ones move up a slot; with at most <c>BeaconRushProtocol.MaxPlayers</c> (8)
    /// players on the board no two share a colour.
    /// </summary>
    public static class PlayerPalette
    {
        /// <summary>How many colours there are, one per seat.</summary>
        public const int Count = 8;

        private static readonly Color32[] Colours =
        {
            new Color32(0xF2, 0x4E, 0x4E, 0xFF), // red
            new Color32(0x3D, 0xA5, 0xF4, 0xFF), // blue
            new Color32(0x6F, 0xD6, 0x4B, 0xFF), // green
            new Color32(0xFF, 0xC8, 0x33, 0xFF), // yellow
            new Color32(0xA8, 0x6B, 0xFF, 0xFF), // violet
            new Color32(0x2E, 0xD3, 0xC2, 0xFF), // teal
            new Color32(0xFF, 0x6F, 0xB8, 0xFF), // pink
            new Color32(0xFF, 0x8A, 0x2E, 0xFF), // orange
        };

        private static readonly string[] Names = { "Red", "Blue", "Green", "Yellow", "Violet", "Teal", "Pink", "Orange" };

        /// <summary>The colour of a slot; any integer wraps into the palette.</summary>
        public static Color32 Colour(int slot) => Colours[Wrap(slot)];

        /// <summary>The colour's name, for example <c>Red</c>.</summary>
        public static string Name(int slot) => Names[Wrap(slot)];

        /// <summary>
        /// The slot of <paramref name="clientId"/>: its rank by client id among <paramref name="present"/> (duplicates count
        /// once), or <c>clientId % Count</c> when it is not among them or there is no list.
        /// </summary>
        public static int SlotFor(ulong clientId, IEnumerable<ulong> present)
        {
            if (present == null)
            {
                return (int)(clientId % Count);
            }

            var earlier = new HashSet<ulong>();
            bool found = false;
            foreach (ulong id in present)
            {
                if (id == clientId)
                {
                    found = true;
                }
                else if (id < clientId)
                {
                    earlier.Add(id);
                }
            }

            return found ? earlier.Count % Count : (int)(clientId % Count);
        }

        private static int Wrap(int slot) => ((slot % Count) + Count) % Count;
    }
}
