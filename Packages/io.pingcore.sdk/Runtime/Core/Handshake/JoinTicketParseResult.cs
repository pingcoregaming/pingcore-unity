namespace PingCore.Core.Handshake
{
    /// <summary>The outcome of <see cref="JoinTicketCodec.Decode"/>: a ticket, or the reason there is none.</summary>
    public readonly struct JoinTicketParseResult
    {
        private JoinTicketParseResult(JoinTicket ticket, JoinRejectReason error, string detail)
        {
            Ticket = ticket;
            Error = error;
            Detail = detail;
        }

        /// <summary>The decoded ticket, or null.</summary>
        public JoinTicket Ticket { get; }

        /// <summary><see cref="JoinRejectReason.None"/> when <see cref="Ticket"/> is set; otherwise one of the <c>payload_*</c> reasons or <see cref="JoinRejectReason.UnsupportedVersion"/>.</summary>
        public JoinRejectReason Error { get; }

        /// <summary>Which rule failed, for diagnostics. Never quotes a payload value.</summary>
        public string Detail { get; }

        /// <summary>True when <see cref="Ticket"/> is set.</summary>
        public bool IsValid => Ticket != null;

        internal static JoinTicketParseResult Ok(JoinTicket ticket) => new JoinTicketParseResult(ticket, JoinRejectReason.None, null);

        internal static JoinTicketParseResult Fail(JoinRejectReason error, string detail) => new JoinTicketParseResult(null, error, detail);
    }
}
