using System;
using System.Collections.Generic;
using PingCore.Core.Handshake;

namespace BeaconRush.Client.Models
{
    /// <summary>
    /// Plain sentences for why a connection did not get in or ended, pure. A game server refuses with one of the 19
    /// reject literals of <see cref="JoinRejectReasons"/> (NGO's disconnect reason); the client adds its own
    /// (<c>connect_timeout</c>, <c>start_failed</c>, <c>disconnected</c>, <c>left</c>), and Beacon Rush ends a session with
    /// <c>session_ended</c> or <c>session_cleared</c>. Anything else is shown as "The connection ended" with the literal.
    /// </summary>
    public static class ConnectionText
    {
        private static readonly Dictionary<string, string> Sentences = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["payload_empty"] = "The game server got an empty join request. Try again; if it keeps happening, update the game.",
            ["payload_too_large"] = "The join request was too large for the game server. Try a shorter display name.",
            ["payload_malformed"] = "The game server could not read the join request. Update the game and try again.",
            ["payload_invalid"] = "The join request was missing something the game server needs. Update the game and try again.",
            ["unsupported_version"] = "The game server expects a newer kind of join request. Update the game.",
            ["kind_not_accepted"] = "This game server does not take that way of joining. Try Quick play, Find match or Browse instead.",
            ["protocol_mismatch"] = "That game server runs a different version of Beacon Rush. Update the game or pick another game server.",
            ["not_in_session"] = "That game server has no match open right now. Try Quick play or Find match.",
            ["allocation_mismatch"] = "That game server has already moved on to another match. Find a new match.",
            ["server_full"] = "That game server is full. Pick another one or try again shortly.",
            ["reservation_invalid"] = "Your seat was not valid any more: it expired or was for another game server. Try again.",
            ["reservation_unverifiable"] = "The game server could not check your seat just now. Try again in a moment.",
            ["not_in_roster"] = "You are not on the player list of that match. Find a new match.",
            ["roster_full"] = "Every seat your ticket brought into that match is already taken.",
            ["backfill_unknown"] = "The game server did not know the match you were sent to fill. Find a new match.",
            ["duplicate_player"] = "You are already in that game from another copy of the game.",
            ["approval_timeout"] = "The game server took too long to let you in. Try again.",
            ["stopping"] = "That game server is shutting down. Find another one.",
            ["refused_by_game"] = "The game turned you away, for example because the match is already showing its results.",
            ["connect_timeout"] = "No answer from the game server. Check the address, and that its game port is open.",
            ["start_failed"] = "The game could not start its network connection. Check the address and try again.",
            ["disconnected"] = "The connection to the game server dropped.",
            ["left"] = "You left the game.",
            ["session_ended"] = "The match is over and the game server closed the session.",
            ["session_cleared"] = "The game server closed the session.",
        };

        /// <summary>The literals the client itself reports, beside the game server's reject literals and session ends.</summary>
        public static readonly IReadOnlyList<string> ClientLiterals = new[] { "connect_timeout", "start_failed", "disconnected", "left" };

        /// <summary>True when <paramref name="literal"/> has a sentence of its own.</summary>
        public static bool IsKnown(string literal) => literal != null && Sentences.ContainsKey(literal);

        /// <summary>The sentence for a refusal or an end; an unknown literal is quoted after a generic sentence.</summary>
        public static string Describe(string literal)
        {
            if (string.IsNullOrEmpty(literal))
            {
                return Sentences["disconnected"];
            }

            return Sentences.TryGetValue(literal, out string sentence) ? sentence : "The connection ended (" + literal + ").";
        }

        /// <summary>A refusal while connecting, for the status line: "Could not join: " and the sentence.</summary>
        public static string Refused(string literal) => "Could not join: " + Describe(literal);
    }
}
