using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PingCore.Core.Handshake;

namespace PingCore.Sdk.Tests.Editor.Handshake
{
    /// <summary>The reject literal table: complete, distinct, and exactly the 19 literals pinned here, the reject reasons https://pingcore.io/docs/fleets/admitting-players lists.</summary>
    public sealed class JoinRejectReasonTableTests
    {
        /// <summary>The documented literals, pinned here so a change to the table is a deliberate change to this list.</summary>
        internal static readonly string[] Documented =
        {
            "payload_empty", "payload_too_large", "payload_malformed", "payload_invalid", "unsupported_version",
            "kind_not_accepted", "protocol_mismatch", "not_in_session", "allocation_mismatch", "server_full",
            "reservation_invalid", "reservation_unverifiable", "not_in_roster", "roster_full", "backfill_unknown",
            "duplicate_player", "approval_timeout", "stopping", "refused_by_game",
        };

        [Test]
        public void TheTableCoversEveryEnumValueOnceWithADistinctLiteral()
        {
            JoinRejectReason[] all = Enum.GetValues(typeof(JoinRejectReason)).Cast<JoinRejectReason>().ToArray();
            Assert.That(JoinRejectReasons.AllReasons(), Is.EquivalentTo(all));
            var seen = new HashSet<string>();
            foreach (JoinRejectReason reason in all)
            {
                string wire = JoinRejectReasons.ToWire(reason);
                Assert.That(wire, Is.Not.EqualTo("unknown"), reason.ToString());
                Assert.That(seen.Add(wire), Is.True, "duplicate literal " + wire);
            }
        }

        [Test]
        public void TheRejectionLiteralsAreExactlyTheDocumentedList()
        {
            Assert.That(JoinRejectReasons.AllWireValues(), Is.EqualTo(Documented));
        }

        [Test]
        public void EveryLiteralParsesBackAndNoneOrJunkDoesNot()
        {
            foreach (string wire in Documented)
            {
                Assert.That(JoinRejectReasons.TryParse(wire, out JoinRejectReason reason), Is.True, wire);
                Assert.That(JoinRejectReasons.ToWire(reason), Is.EqualTo(wire));
            }

            Assert.That(JoinRejectReasons.TryParse("none", out _), Is.False);
            Assert.That(JoinRejectReasons.TryParse("ROSTER_FULL", out _), Is.False);
            Assert.That(JoinRejectReasons.TryParse(null, out _), Is.False);
            Assert.That(JoinRejectReasons.ToWire((JoinRejectReason)999), Is.EqualTo("unknown"));
        }

        [Test]
        public void KindLiteralsRoundTripExactly()
        {
            foreach (JoinTicketKind kind in Enum.GetValues(typeof(JoinTicketKind)))
            {
                Assert.That(JoinTicketKinds.TryParse(JoinTicketKinds.ToWire(kind), out JoinTicketKind back), Is.True);
                Assert.That(back, Is.EqualTo(kind));
            }

            Assert.That(JoinTicketKinds.TryParse("Match", out _), Is.False);
        }
    }
}
