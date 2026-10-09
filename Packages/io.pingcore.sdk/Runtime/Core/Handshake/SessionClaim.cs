using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Core.Handshake
{
    /// <summary>What claiming a session for a hosted <c>reservation</c> join came to (<see cref="AdmissionFacts.SessionClaim"/>).</summary>
    public enum SessionClaimOutcome
    {
        /// <summary>No claim was made: the game did not opt in, not a hosted reservation, or the game server already had a current allocation.</summary>
        None = 0,

        /// <summary>The game server claimed a session of its own (a self-allocation), or joined the one it had just claimed for another joiner.</summary>
        Claimed = 1,

        /// <summary>
        /// An allocation the platform made (a match or a backend allocation) became current between the evidence and the
        /// claim, so no self-allocation was made. The gate is asked again with this outcome and the new
        /// <see cref="AdmissionFacts.CurrentAllocationId"/>: it decides whether the reservation joiner may join that session.
        /// </summary>
        AllocatedMeanwhile = 2,

        /// <summary>The claim failed (the local SDK endpoint refused or did not answer, or the frame never came): <c>refused_by_game</c>.</summary>
        Failed = 3,
    }

    /// <summary>The answer of <see cref="ISessionClaimEvidence.ClaimSessionAsync"/>.</summary>
    public readonly struct SessionClaimResult
    {
        private SessionClaimResult(SessionClaimOutcome outcome, string allocationId, string detail)
        {
            Outcome = outcome;
            AllocationId = allocationId;
            Detail = detail;
        }

        /// <summary>What the claim came to; never <see cref="SessionClaimOutcome.None"/> from an evidence.</summary>
        public SessionClaimOutcome Outcome { get; }

        /// <summary>The allocation that is now current: the self-allocation for <see cref="SessionClaimOutcome.Claimed"/>, the platform's for <see cref="SessionClaimOutcome.AllocatedMeanwhile"/>; null on failure.</summary>
        public string AllocationId { get; }

        /// <summary>Diagnostics only.</summary>
        public string Detail { get; }

        /// <summary>The game server is now in a session of its own, <paramref name="allocationId"/>.</summary>
        public static SessionClaimResult Claimed(string allocationId) => new SessionClaimResult(SessionClaimOutcome.Claimed, allocationId, null);

        /// <summary>The platform's allocation <paramref name="allocationId"/> became current first; nothing was claimed.</summary>
        public static SessionClaimResult AllocatedMeanwhile(string allocationId) => new SessionClaimResult(SessionClaimOutcome.AllocatedMeanwhile, allocationId, null);

        /// <summary>The claim did not happen, or could not be confirmed.</summary>
        public static SessionClaimResult Failed(string detail) => new SessionClaimResult(SessionClaimOutcome.Failed, null, detail);
    }

    /// <summary>
    /// The second half of the hosted reservation rule: an <see cref="IAdmissionEvidence"/> that can also claim an idle
    /// game server for the session a <c>reservation</c> join starts. On a PingCore-hosted game server that is a
    /// self-allocation through the local SDK endpoint (<c>PingCore.Fleet.Sessions.HostedAdmissionEvidence</c>), so the
    /// game server reads <c>in_session</c> and the matchmaker stops allocating it before the player is let in.
    /// <see cref="AdmissionPipeline"/> calls it only when the game set <see cref="ApprovalOptions.ClaimIdleSessions"/>, after
    /// the table and the gate accepted the join and its seat is held, and before the approval is returned. The joiner is
    /// admitted on the hold, never on the self-allocation's id.
    /// </summary>
    public interface ISessionClaimEvidence
    {
        /// <summary>
        /// Claims a session for <paramref name="ticket"/> (a <c>reservation</c> join on a game server whose evidence found
        /// no current allocation). Called on the main thread inside the decision deadline; must not throw for an HTTP or
        /// transport failure (answer <see cref="SessionClaimResult.Failed"/> instead) and must honour
        /// <paramref name="cancellationToken"/>. Concurrent claims must share one session.
        /// </summary>
        Task<SessionClaimResult> ClaimSessionAsync(JoinTicket ticket, AdmissionFacts facts, CancellationToken cancellationToken);
    }
}
