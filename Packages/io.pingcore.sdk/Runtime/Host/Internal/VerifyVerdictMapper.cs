using System;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Discovery.Host
{
    /// <summary>
    /// Turns a verify answer into a <see cref="VerifyVerdict"/>, pure. The SDK always sends its own
    /// <c>serverId</c> and the joining <c>playerId</c>:
    /// <list type="bullet">
    /// <item>No answer, or any status but 200 (429, 503, 401, 403, a 5xx, a transport failure, a cancellation): <see cref="VerifyVerdict.Unavailable"/>, never valid.</item>
    /// <item>Verdict only (<c>{valid}</c> alone, what an open app's shipped token gets; Discovery admitted the seat itself): <see cref="VerifyVerdict.Valid"/> or <see cref="VerifyVerdict.Invalid"/>, <c>Detailed</c> false.</item>
    /// <item>Detailed <c>valid: true</c>: <see cref="VerifyVerdict.Valid"/> only when its <c>serverId</c> equals ours (else <see cref="VerifyVerdict.WrongServer"/>) and its <c>playerIds</c> is null or contains the player (else <see cref="VerifyVerdict.NotInReservation"/>). The SDK checks what Discovery already checked, so a wrong answer can never admit.</item>
    /// <item>Detailed <c>valid: false</c>: <c>reason</c> <c>wrong_server</c> or <c>not_in_reservation</c> maps to its verdict, any other to <see cref="VerifyVerdict.Invalid"/>.</item>
    /// </list>
    /// </summary>
    internal static class VerifyVerdictMapper
    {
        /// <summary>The verdict for one verify call.</summary>
        /// <param name="call">The call result.</param>
        /// <param name="response">The parsed answer on success, else null.</param>
        /// <param name="ownServerId">The serverId this game server heartbeats with.</param>
        /// <param name="playerId">The joining player sent with the call.</param>
        public static VerifyResult Map(DiscoveryCallResult call, VerifyReservationResponse response, string ownServerId, string playerId)
        {
            if (call == null || !call.IsOk || response == null)
            {
                return new VerifyResult(VerifyVerdict.Unavailable, false, null, call);
            }

            bool detailed = IsDetailed(response);
            if (!response.Valid)
            {
                switch (response.ReasonCode)
                {
                    case DiscoveryReason.WrongServer:
                        return new VerifyResult(VerifyVerdict.WrongServer, true, response, call);
                    case DiscoveryReason.NotInReservation:
                        return new VerifyResult(VerifyVerdict.NotInReservation, true, response, call);
                    default:
                        return new VerifyResult(VerifyVerdict.Invalid, detailed, response, call);
                }
            }

            if (!detailed)
            {
                return new VerifyResult(VerifyVerdict.Valid, false, response, call);
            }

            if (ownServerId == null || !string.Equals(response.ServerId, ownServerId, StringComparison.Ordinal))
            {
                return new VerifyResult(VerifyVerdict.WrongServer, true, response, call);
            }

            if (response.PlayerIds != null && !response.PlayerIds.Contains(playerId))
            {
                return new VerifyResult(VerifyVerdict.NotInReservation, true, response, call);
            }

            return new VerifyResult(VerifyVerdict.Valid, true, response, call);
        }

        /// <summary>True when the answer carries anything beside <c>valid</c>: a <c>reason</c> or any hold field.</summary>
        public static bool IsDetailed(VerifyReservationResponse response)
        {
            return response.Reason != null
                || response.ReservationId != null
                || response.ServerId != null
                || response.Seats.HasValue
                || response.PlayerIdsSpecified
                || response.ContextSpecified
                || response.ExpiresAt.HasValue
                || response.OwnerKind != null
                || response.OwnerPlayerIdSpecified;
        }
    }
}
