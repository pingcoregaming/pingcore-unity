using System.Collections.Generic;
using BeaconRush.Client.Models;
using NUnit.Framework;
using PingCore.Core.Handshake;

namespace BeaconRush.Client.Tests
{
    /// <summary>Refusals and session ends in plain words: every reject literal, the client's own and Beacon Rush's session ends.</summary>
    public sealed class ConnectionTextTests
    {
        [Test]
        public void EveryOneOfTheNineteenRejectLiteralsHasItsOwnPlainSentence()
        {
            IReadOnlyList<string> literals = JoinRejectReasons.AllWireValues();
            Assert.That(literals.Count, Is.EqualTo(19));
            var sentences = new HashSet<string>();
            foreach (string literal in literals)
            {
                Assert.That(ConnectionText.IsKnown(literal), Is.True, literal);
                string sentence = ConnectionText.Describe(literal);
                Assert.That(sentence, Does.Not.Contain(literal), "a sentence never quotes its literal: " + literal);
                Assert.That(sentence, Does.Not.Contain("_"), literal);
                Assert.That(sentence, Does.EndWith("."), literal);
                Assert.That(sentences.Add(sentence), Is.True, "two literals share a sentence: " + literal);
            }
        }

        [TestCase("server_full", "That game server is full. Pick another one or try again shortly.")]
        [TestCase("protocol_mismatch", "That game server runs a different version of Beacon Rush. Update the game or pick another game server.")]
        [TestCase("not_in_session", "That game server has no match open right now. Try Quick play or Find match.")]
        [TestCase("connect_timeout", "No answer from the game server. Check the address, and that its game port is open.")]
        [TestCase("session_ended", "The match is over and the game server closed the session.")]
        public void ALiteralReadsAsItsSentence(string literal, string sentence)
        {
            Assert.That(ConnectionText.Describe(literal), Is.EqualTo(sentence));
        }

        [Test]
        public void TheClientsOwnLiteralsAndTheSessionEndsAreKnown()
        {
            Assert.That(ConnectionText.ClientLiterals, Is.EqualTo(new[] { "connect_timeout", "start_failed", "disconnected", "left" }));
            foreach (string literal in new[] { "connect_timeout", "start_failed", "disconnected", "left", "session_ended", "session_cleared" })
            {
                Assert.That(ConnectionText.IsKnown(literal), Is.True, literal);
                Assert.That(ConnectionText.Describe(literal), Does.Not.Contain("_"), literal);
            }
        }

        [Test]
        public void AnUnknownLiteralIsQuotedAfterAGenericSentence()
        {
            Assert.That(ConnectionText.IsKnown("brand_new_reason"), Is.False);
            Assert.That(ConnectionText.Describe("brand_new_reason"), Is.EqualTo("The connection ended (brand_new_reason)."));
            Assert.That(ConnectionText.IsKnown(null), Is.False);
        }

        [Test]
        public void NoReasonReadsAsADroppedConnection()
        {
            Assert.That(ConnectionText.Describe(null), Is.EqualTo("The connection to the game server dropped."));
            Assert.That(ConnectionText.Describe(string.Empty), Is.EqualTo("The connection to the game server dropped."));
        }

        [Test]
        public void ARefusalIsPrefixedForTheStatusLine()
        {
            Assert.That(ConnectionText.Refused("stopping"), Is.EqualTo("Could not join: That game server is shutting down. Find another one."));
        }
    }
}
