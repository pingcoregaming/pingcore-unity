namespace PingCore.Core.Handshake
{
    /// <summary>
    /// The game's own admission rule, asked after the decision table accepted a ticket: is there a
    /// session to join and a seat in it? It answers <see cref="AdmissionGateResult.Admit"/> or a
    /// rejection with <see cref="JoinRejectReason.NotInSession"/>, <see cref="JoinRejectReason.ServerFull"/>
    /// or <see cref="JoinRejectReason.RefusedByGame"/>; any other reason is reported as
    /// <c>refused_by_game</c>. Called synchronously on the main thread, inside the decision deadline.
    /// </summary>
    public interface IAdmissionGate
    {
        /// <summary>Decides session and seats for an accepted ticket. <paramref name="facts"/> holds what the evidence found (mode, current allocation, hold context, roster).</summary>
        AdmissionGateResult CanAdmit(JoinTicket ticket, AdmissionFacts facts);
    }

    /// <summary>The answer of an <see cref="IAdmissionGate"/>.</summary>
    public readonly struct AdmissionGateResult
    {
        private AdmissionGateResult(bool admitted, JoinRejectReason reason, string detail)
        {
            Admitted = admitted;
            Reason = reason;
            Detail = detail;
        }

        /// <summary>Admit the connection.</summary>
        public static AdmissionGateResult Admit => new AdmissionGateResult(true, JoinRejectReason.None, null);

        /// <summary>True to admit.</summary>
        public bool Admitted { get; }

        /// <summary>Why not; <see cref="JoinRejectReason.None"/> when admitted.</summary>
        public JoinRejectReason Reason { get; }

        /// <summary>Diagnostics; never a payload value.</summary>
        public string Detail { get; }

        /// <summary>Refuse with <c>not_in_session</c>, <c>server_full</c> or <c>refused_by_game</c>; another reason becomes <c>refused_by_game</c>.</summary>
        public static AdmissionGateResult Reject(JoinRejectReason reason, string detail = null)
        {
            JoinRejectReason allowed = reason == JoinRejectReason.NotInSession || reason == JoinRejectReason.ServerFull
                ? reason
                : JoinRejectReason.RefusedByGame;
            return new AdmissionGateResult(false, allowed, detail);
        }
    }

    /// <summary>A gate that admits every connection the decision table accepted (seats are then only the table's).</summary>
    public sealed class AdmitAllGate : IAdmissionGate
    {
        /// <inheritdoc />
        public AdmissionGateResult CanAdmit(JoinTicket ticket, AdmissionFacts facts) => AdmissionGateResult.Admit;
    }
}
