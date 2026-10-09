using System.Collections.Generic;
using BeaconRush.Match;
using NUnit.Framework;
using UnityEngine;

namespace BeaconRush.Tests.Editor
{
    /// <summary>The client-side player colours: eight fixed colours, handed out by join order among the score board's players.</summary>
    public sealed class PlayerPaletteTests
    {
        [Test]
        public void TheEightColoursAreFixedAndDistinct()
        {
            Assert.That(PlayerPalette.Count, Is.EqualTo(8));
            Assert.That(PlayerPalette.Colour(0), Is.EqualTo(new Color32(0xF2, 0x4E, 0x4E, 0xFF)));
            Assert.That(PlayerPalette.Colour(1), Is.EqualTo(new Color32(0x3D, 0xA5, 0xF4, 0xFF)));
            Assert.That(PlayerPalette.Colour(7), Is.EqualTo(new Color32(0xFF, 0x8A, 0x2E, 0xFF)));
            var seen = new HashSet<Color32>();
            for (int slot = 0; slot < PlayerPalette.Count; slot++)
            {
                Assert.That(seen.Add(PlayerPalette.Colour(slot)), Is.True, "slot " + slot + " repeats a colour");
            }
        }

        [Test]
        public void ASlotOutsideThePaletteWraps()
        {
            Assert.That(PlayerPalette.Colour(8), Is.EqualTo(PlayerPalette.Colour(0)));
            Assert.That(PlayerPalette.Colour(-1), Is.EqualTo(PlayerPalette.Colour(7)));
            Assert.That(PlayerPalette.Name(9), Is.EqualTo("Blue"));
        }

        [Test]
        public void PlayersAreColouredInTheOrderTheyJoined()
        {
            var board = new List<ulong> { 9, 5, 7 };
            Assert.That(PlayerPalette.SlotFor(5, board), Is.EqualTo(0));
            Assert.That(PlayerPalette.SlotFor(7, board), Is.EqualTo(1));
            Assert.That(PlayerPalette.SlotFor(9, board), Is.EqualTo(2));
        }

        [Test]
        public void EightPlayersWhoseIdsShareARemainderStillGetEightColours()
        {
            // Client ids 3, 11, ..., 59 are all 3 modulo 8: colouring by id alone would paint every one the same.
            var board = new List<ulong> { 3, 11, 19, 27, 35, 43, 51, 59 };
            var slots = new HashSet<int>();
            foreach (ulong id in board)
            {
                slots.Add(PlayerPalette.SlotFor(id, board));
            }

            Assert.That(slots, Is.EquivalentTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
        }

        [Test]
        public void WhenAnEarlierPlayerLeavesTheLaterOnesMoveUpASlot()
        {
            Assert.That(PlayerPalette.SlotFor(7, new List<ulong> { 5, 7, 9 }), Is.EqualTo(1));
            Assert.That(PlayerPalette.SlotFor(7, new List<ulong> { 7, 9 }), Is.EqualTo(0));
        }

        [Test]
        public void ARepeatedIdCountsOnce()
        {
            Assert.That(PlayerPalette.SlotFor(9, new List<ulong> { 5, 5, 9 }), Is.EqualTo(1));
        }

        [Test]
        public void APlayerNotOnTheBoardFallsBackToItsIdModuloEight()
        {
            Assert.That(PlayerPalette.SlotFor(13, new List<ulong> { 1, 2 }), Is.EqualTo(5));
            Assert.That(PlayerPalette.SlotFor(13, null), Is.EqualTo(5));
            Assert.That(PlayerPalette.SlotFor(13, new List<ulong>()), Is.EqualTo(5));
        }
    }
}
