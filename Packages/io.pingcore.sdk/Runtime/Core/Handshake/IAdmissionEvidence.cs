using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// Where a game server's admission evidence comes from, per hosting mode: the local SDK shim on a
    /// PingCore-hosted game server (<c>PingCore.Fleet.Sessions.HostedAdmissionEvidence</c>), verify on a
    /// self-hosted game server or online listen host (<c>PingCore.Netcode.NGO.HeartbeatAdmissionEvidence</c>),
    /// nothing on a LAN-only listen host (<see cref="LanAdmissionEvidence"/>).
    /// </summary>
    public interface IAdmissionEvidence
    {
        /// <summary>The evidence source written to <see cref="AdmissionFacts.EvidenceSource"/> and the approval event: <c>fleet</c>, <c>heartbeat</c> or <c>lan</c>.</summary>
        string Source { get; }

        /// <summary>True when the game server behind this evidence is stopping (for example the local SDK shim is stopping or shutting down).</summary>
        bool IsStopping { get; }

        /// <summary>
        /// Fills the evidence fields of <paramref name="facts"/> for <paramref name="ticket"/> (a hold, a
        /// verify verdict, the current allocation and roster, a delivered backfill). The pipeline has
        /// already set the game's own fields and runs the protocol and stopping checks first. Called on
        /// the main thread; must not throw for an HTTP or transport failure (record it as evidence
        /// instead) and must honour <paramref name="cancellationToken"/>, which the deadline cancels.
        /// </summary>
        Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken);
    }

    /// <summary>The evidence of a listen host started LAN only: there is none to gather. It accepts <c>lan</c> tickets only (<see cref="JoinAdmission"/>).</summary>
    public sealed class LanAdmissionEvidence : IAdmissionEvidence
    {
        /// <inheritdoc />
        public string Source => "lan";

        /// <inheritdoc />
        public bool IsStopping => false;

        /// <inheritdoc />
        public Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
