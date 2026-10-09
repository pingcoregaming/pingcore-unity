using System.Collections.Generic;
using System.IO;
using BeaconRush.Client.Flows;
using NUnit.Framework;
using UnityEngine;

namespace BeaconRush.Client.Tests
{
    /// <summary>
    /// The interactive client's token store profile: two copies of the game on one PC must be two players. The
    /// argument wins; otherwise each concurrent copy takes the first free instance slot (slot 0 being the empty profile a
    /// single copy has always used); with every slot held, a session-only random id.
    /// </summary>
    public sealed class InteractiveProfileTests
    {
        [Test]
        public void TheArgumentIsReadCaseInsensitivelyCleanedAndTheLastOneWins()
        {
            Assert.That(InteractiveProfile.FromArgs(new[] { "Game.exe", "-pingcoreProfile", "alice" }), Is.EqualTo("alice"));
            Assert.That(InteractiveProfile.FromArgs(new[] { "-PINGCOREPROFILE", "a.b:c" }), Is.EqualTo("a-b-c"), "only the store key's characters");
            Assert.That(InteractiveProfile.FromArgs(new[] { "-pingcoreProfile", "one", "-pingcoreProfile", "two" }), Is.EqualTo("two"));
            Assert.That(InteractiveProfile.FromArgs(new[] { "-pingcoreProfile", new string('x', 60) }), Has.Length.EqualTo(InteractiveProfile.MaxLength));
            Assert.That(InteractiveProfile.FromArgs(new[] { "-pingcoreProfile" }), Is.Null, "no value");
            Assert.That(InteractiveProfile.FromArgs(new[] { "-pingcoreProfile", "-logFile" }), Is.Null, "a flag is never a value");
            Assert.That(InteractiveProfile.FromArgs(new[] { "-pingcoreProfile", "a b" }), Is.Null, "not an id");
            Assert.That(InteractiveProfile.FromArgs(new string[0]), Is.Null);
            Assert.That(InteractiveProfile.FromArgs(null), Is.Null);
        }

        [Test]
        public void TheArgumentWinsAndNoSlotIsClaimedForIt()
        {
            var asked = new List<int>();
            string profile = InteractiveProfile.Resolve("alice", slot => { asked.Add(slot); return true; }, () => "unused", out InteractiveProfile.Source source);
            Assert.That(profile, Is.EqualTo("alice"));
            Assert.That(source, Is.EqualTo(InteractiveProfile.Source.Argument));
            Assert.That(asked, Is.Empty);
        }

        [Test]
        public void TheFirstFreeSlotIsTakenAndSlotZeroIsTheDefaultPlayer()
        {
            var held = new HashSet<int> { 0, 1 };
            string third = InteractiveProfile.Resolve(null, slot => !held.Contains(slot), () => "unused", out InteractiveProfile.Source source);
            Assert.That(third, Is.EqualTo("instance-2"));
            Assert.That(source, Is.EqualTo(InteractiveProfile.Source.Slot));
            Assert.That(InteractiveProfile.Resolve(null, slot => true, () => "unused", out _), Is.EqualTo(string.Empty), "a single copy keeps the player it always had");
        }

        [Test]
        public void WithEverySlotHeldTheProfileIsASessionOnlyId()
        {
            string profile = InteractiveProfile.Resolve(null, slot => false, () => "0123456789abcdef0123456789abcdef", out InteractiveProfile.Source source);
            Assert.That(profile, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(source, Is.EqualTo(InteractiveProfile.Source.Session));
            string fresh = InteractiveProfile.Resolve(null, null, null, out _);
            Assert.That(fresh, Does.Match("^[A-Za-z0-9_-]{16,32}$"), "a fresh random id by default, in the store key's characters");
            Assert.That(InteractiveProfile.Resolve(null, null, null, out _), Is.Not.EqualTo(fresh), "fresh each time");
        }

        [Test]
        public void SlotLocksAreOnOnlyWhereFileShareNoneHoldsAcrossProcesses()
        {
            bool windows = Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer;
            Assert.That(ProfileSlotLocks.Supported, Is.EqualTo(windows));
            if (!windows)
            {
                using (var locks = new ProfileSlotLocks(Path.GetTempPath()))
                {
                    Assert.That(InteractiveProfile.Resolve(null, locks.TryClaim, () => "session-id", out InteractiveProfile.Source source), Is.EqualTo("session-id"));
                    Assert.That(source, Is.EqualTo(InteractiveProfile.Source.Session));
                }
            }
        }

        [Test]
        public void TwoProcessesWorthOfSlotLocksGetDifferentSlotsAndAReleasedSlotIsFreeAgain()
        {
            Assume.That(ProfileSlotLocks.Supported, "slot locks hold across processes only on Windows");
            string folder = Path.Combine(Path.GetTempPath(), "beaconrush-profile-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                using (var first = new ProfileSlotLocks(folder))
                using (var second = new ProfileSlotLocks(folder))
                {
                    Assert.That(InteractiveProfile.Resolve(null, first.TryClaim, null, out _), Is.EqualTo(string.Empty));
                    Assert.That(InteractiveProfile.Resolve(null, second.TryClaim, null, out _), Is.EqualTo("instance-1"), "the second copy is another player");
                    Assert.That(first.TryClaim(5), Is.False, "one slot per copy");
                }

                using (var again = new ProfileSlotLocks(folder))
                {
                    Assert.That(InteractiveProfile.Resolve(null, again.TryClaim, null, out _), Is.EqualTo(string.Empty), "the first copy's slot was released");
                }
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        [Test]
        public void AMultiplayerPlayModeVirtualPlayerPlaysUnderItsTag()
        {
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, new[] { "Player2" }), Is.EqualTo("mppm-Player2"));
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, new[] { "", "  ", "Red Team" }), Is.EqualTo("mppm-Red-Team"), "the first usable tag, cleaned");
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, new[] { new string('x', 60) }), Has.Length.EqualTo(InteractiveProfile.MaxLength));
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, new[] { "!!!" }), Is.Null, "nothing usable left after cleaning");
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, new string[0]), Is.Null, "no tag and no instance id: an instance slot");
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, new string[0], "vp-3f2a"), Is.EqualTo("mppm-vp-3f2a"), "an untagged instance plays under its id");
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, new[] { "Player2" }, "vp-3f2a"), Is.EqualTo("mppm-Player2"), "the tag wins over the id");
            Assert.That(InteractiveProfile.FromVirtualPlayer(false, new[] { "Player2" }, "vp-3f2a"), Is.Null, "the main Editor keeps its own profile");
            Assert.That(InteractiveProfile.FromVirtualPlayer(true, null), Is.Null);
            Assert.That(VirtualPlayerTags.InstanceIdFromArgs(new[] { "Unity.exe", "-scenarioClone", "-name", "Player 2", "-vpId=mppm9bd9b493" }), Is.EqualTo("mppm9bd9b493"),
                "how Multiplayer Play Mode 2.x starts an additional Editor instance");
            Assert.That(VirtualPlayerTags.InstanceIdFromArgs(new[] { "-vpId=" }), Is.Null, "an empty value");
            Assert.That(VirtualPlayerTags.InstanceIdFromArgs(new[] { "-vp-channel-name=vp-channel" }), Is.Null, "a near miss");
            Assert.That(VirtualPlayerTags.InstanceIdFromArgs(new[] { "Unity.exe", "-VPID", "abc_1" }), Is.EqualTo("abc_1"));
            Assert.That(VirtualPlayerTags.InstanceIdFromArgs(new[] { "-vpId", "-batchmode" }), Is.Null, "a flag is never a value");
            Assert.That(VirtualPlayerTags.InstanceIdFromArgs(new[] { "-vpId" }), Is.Null);
            Assert.That(VirtualPlayerTags.InstanceIdFromArgs(null), Is.Null);
        }

        [Test]
        public void TheArgumentBeatsTheVirtualPlayerAndTheVirtualPlayerBeatsTheSlots()
        {
            var asked = new List<int>();
            string tagged = InteractiveProfile.Resolve(null, "mppm-Player2", slot => { asked.Add(slot); return true; }, null, out InteractiveProfile.Source source);
            Assert.That(tagged, Is.EqualTo("mppm-Player2"));
            Assert.That(source, Is.EqualTo(InteractiveProfile.Source.VirtualPlayer));
            Assert.That(asked, Is.Empty, "a tagged virtual player claims no slot");

            Assert.That(InteractiveProfile.Resolve("alice", "mppm-Player2", slot => true, null, out source), Is.EqualTo("alice"));
            Assert.That(source, Is.EqualTo(InteractiveProfile.Source.Argument));

            // The main Editor (no virtual player profile) and a tagged virtual player are two players, whatever slot the Editor holds.
            string main = InteractiveProfile.Resolve(null, InteractiveProfile.FromVirtualPlayer(false, new[] { "Player2" }), slot => true, null, out _);
            string second = InteractiveProfile.Resolve(null, InteractiveProfile.FromVirtualPlayer(true, new[] { "Player2" }), slot => true, null, out _);
            Assert.That(main, Is.EqualTo(string.Empty));
            Assert.That(second, Is.Not.EqualTo(main));
        }

        [Test]
        public void OutsideMultiplayerPlayModeTheReaderReportsAMainEditorWithNoTags()
        {
            // The EditMode run is the main Editor: whether or not the package is installed, it is never a virtual player.
            Assert.That(VirtualPlayerTags.IsAdditionalEditor, Is.False);
            Assert.That(VirtualPlayerTags.InstanceId, Is.Null);
            Assert.That(InteractiveProfile.FromVirtualPlayer(VirtualPlayerTags.IsAdditionalEditor, VirtualPlayerTags.Tags, VirtualPlayerTags.InstanceId), Is.Null);
        }
    }
}
