using System;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// One connection's admission, engine-neutral: decode, the protocol and stopping checks, the
    /// evidence (raced against the deadline), the decision table, the game's gate, the ledger hold, and,
    /// for a hosted <c>reservation</c> join on an idle game server when the game opted in
    /// (<see cref="ApprovalOptions.ClaimIdleSessions"/>), the session claim
    /// (<see cref="JoinAdmission.NeedsSessionClaim"/>, through <see cref="ISessionClaimEvidence"/>) before the approval.
    /// <c>PingCore.Netcode.NGO.PingCoreConnectionApproval</c> drives it from NGO's approval callback; an
    /// adapter for another netcode does the same. Never throws for a payload, an evidence failure or a
    /// gate failure: every path ends in an <see cref="AdmissionDecision"/>. Run it on the main thread.
    /// </summary>
    public sealed class AdmissionPipeline
    {
        private readonly ApprovalOptions options;
        private readonly IAdmissionEvidence evidence;
        private readonly IAdmissionGate gate;
        private readonly IScheduler scheduler;
        private readonly object decideGate = new object();
        private volatile bool stopping;

        /// <summary>Creates a pipeline.</summary>
        /// <param name="options">Copied; later changes have no effect.</param>
        /// <param name="evidence">Where the evidence comes from; must suit <see cref="ApprovalOptions.Mode"/>.</param>
        /// <param name="gate">The game's own rule; null admits whatever the table accepts.</param>
        /// <param name="scheduler">The clock and delays for the deadline; <see cref="ApprovalOptions.Scheduler"/> is ignored here.</param>
        /// <param name="ledger">The ledger to hold seats in; null creates one.</param>
        public AdmissionPipeline(ApprovalOptions options, IAdmissionEvidence evidence, IAdmissionGate gate, IScheduler scheduler, AdmissionLedger ledger = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.Deadline <= TimeSpan.Zero)
            {
                throw new ArgumentException("the deadline must be positive", nameof(options));
            }

            this.options = options.Clone();
            this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            this.gate = gate ?? new AdmitAllGate();
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Ledger = ledger ?? new AdmissionLedger();
        }

        /// <summary>The seats held by admitted connections.</summary>
        public AdmissionLedger Ledger { get; }

        /// <summary>The options it was created with (a copy).</summary>
        public ApprovalOptions Options => options.Clone();

        /// <summary>True after <see cref="NotifyStopping"/> or while the evidence reports stopping.</summary>
        public bool IsStopping => stopping || evidence.IsStopping;

        /// <summary>From now on every connection is <c>stopping</c>. Call it from <c>Application.quitting</c>. Idempotent.</summary>
        public void NotifyStopping() => stopping = true;

        /// <summary>
        /// Decides one connection. Cancelling <paramref name="cancellationToken"/> (the connection closed)
        /// ends the evidence and returns a rejection; an approved decision has already taken its seats in
        /// <see cref="Ledger"/>, which the caller gives back with <see cref="Release"/> when the
        /// connection ends or cannot be approved after all.
        /// </summary>
        public async Task<AdmissionDecision> AdmitAsync(ulong connectionId, byte[] payload, CancellationToken cancellationToken)
        {
            DateTimeOffset started = scheduler.UtcNow;
            JoinTicketParseResult parsed = JoinTicketCodec.Decode(payload);
            if (!parsed.IsValid)
            {
                return Finish(AdmissionDecision.Reject(parsed.Error, parsed.Detail, options.Mode, evidence.Source), connectionId, started);
            }

            JoinTicket ticket = parsed.Ticket;
            AdmissionFacts facts = NewFacts();
            AdmissionDecision early = JoinAdmission.Precheck(ticket, facts.ProtocolVersion, facts.Stopping, facts);
            if (early != null)
            {
                return Finish(early, connectionId, started);
            }

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<Exception> gather = GatherSafely(ticket, facts, deadline.Token);
                Task timer = scheduler.DelayAsync(options.Deadline, deadline.Token);
                Task first = await Task.WhenAny(gather, timer);
                deadline.Cancel();
                if (cancellationToken.IsCancellationRequested)
                {
                    return Finish(AdmissionDecision.Reject(JoinRejectReason.ApprovalTimeout,
                        "the approval was cancelled before a decision (the connection closed or the approval was disposed)", ticket, facts), connectionId, started);
                }

                if (first != gather)
                {
                    return Finish(AdmissionDecision.Reject(JoinRejectReason.ApprovalTimeout,
                        "no decision within " + (long)options.Deadline.TotalMilliseconds + " ms", ticket, facts), connectionId, started);
                }

                Exception failure = await gather;
                if (failure != null)
                {
                    return Finish(AdmissionDecision.Reject(JoinRejectReason.RefusedByGame,
                        "the admission evidence failed (" + failure.GetType().Name + ")", ticket, facts), connectionId, started);
                }
            }

            // One synchronous step from the ledger snapshot to the hold, so two pending approvals for
            // one ticket or hold cannot both take its last seat (locked too, in case a caller is not on
            // the main thread).
            AdmissionDecision decision;
            lock (decideGate)
            {
                decision = DecideAndHold(connectionId, ticket, facts);
            }

            if (decision.Approved && JoinAdmission.NeedsSessionClaim(ticket, facts))
            {
                decision = await ClaimSessionAsync(connectionId, ticket, facts, decision, started, cancellationToken);
            }

            return Finish(decision, connectionId, started);
        }

        /// <summary>Gives back the seats of <paramref name="connectionId"/> (it disconnected, or its approval could not be delivered). Idempotent.</summary>
        public bool Release(ulong connectionId) => Ledger.Release(connectionId);

        private AdmissionDecision DecideAndHold(ulong connectionId, JoinTicket ticket, AdmissionFacts facts)
        {
            facts.Stopping = facts.Stopping || IsStopping;
            Ledger.Snapshot(ticket, facts);
            AdmissionDecision decision = JoinAdmission.Decide(ticket, facts);
            if (decision.Approved)
            {
                AdmissionGateResult verdict = AskGate(ticket, facts);

                if (!verdict.Admitted)
                {
                    decision = decision.AsRejection(verdict.Reason, verdict.Detail ?? "the game refused");
                }
            }

            if (decision.Approved && !Ledger.Hold(connectionId, ticket))
            {
                decision = decision.AsRejection(JoinRejectReason.DuplicatePlayer, "this connection is already admitted");
            }

            return decision;
        }

        /// <summary>
        /// A hosted reservation accepted on an idle game server: claim its session (inside what is left of the deadline)
        /// before the approval. The seat is already held; every path that does not approve gives it back.
        /// </summary>
        private async Task<AdmissionDecision> ClaimSessionAsync(ulong connectionId, JoinTicket ticket, AdmissionFacts facts, AdmissionDecision accepted,
            DateTimeOffset started, CancellationToken cancellationToken)
        {
            if (!(evidence is ISessionClaimEvidence claimer))
            {
                return Refuse(connectionId, accepted, JoinRejectReason.RefusedByGame,
                    "an idle hosted game server admits a reservation only once it has claimed a session, and this evidence cannot claim one", SessionClaimOutcome.Failed);
            }

            TimeSpan left = options.Deadline - (scheduler.UtcNow - started);
            if (left <= TimeSpan.Zero)
            {
                return Refuse(connectionId, accepted, JoinRejectReason.ApprovalTimeout,
                    "no decision within " + (long)options.Deadline.TotalMilliseconds + " ms", SessionClaimOutcome.Failed);
            }

            SessionClaimResult claim;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<ClaimAttempt> claiming = ClaimSafely(claimer, ticket, facts, deadline.Token);
                Task timer = scheduler.DelayAsync(left, deadline.Token);
                Task first = await Task.WhenAny(claiming, timer);
                deadline.Cancel();
                if (cancellationToken.IsCancellationRequested)
                {
                    return Refuse(connectionId, accepted, JoinRejectReason.ApprovalTimeout,
                        "the approval was cancelled while the game server claimed a session (the connection closed or the approval was disposed)", SessionClaimOutcome.Failed);
                }

                if (first != claiming)
                {
                    return Refuse(connectionId, accepted, JoinRejectReason.ApprovalTimeout,
                        "no decision within " + (long)options.Deadline.TotalMilliseconds + " ms (the session claim did not finish)", SessionClaimOutcome.Failed);
                }

                ClaimAttempt attempt = await claiming;
                if (attempt.Failure != null)
                {
                    return Refuse(connectionId, accepted, JoinRejectReason.RefusedByGame,
                        "the session claim failed (" + attempt.Failure.GetType().Name + ")", SessionClaimOutcome.Failed);
                }

                claim = attempt.Answer;
            }

            if (IsStopping)
            {
                // The game server began stopping while it claimed: it takes no one new, whatever the claim came to.
                return Refuse(connectionId, accepted, JoinRejectReason.Stopping, "this game server is stopping",
                    claim.Outcome == SessionClaimOutcome.None ? SessionClaimOutcome.Failed : claim.Outcome);
            }

            switch (claim.Outcome)
            {
                case SessionClaimOutcome.Claimed:
                    facts.SessionClaim = SessionClaimOutcome.Claimed;
                    facts.CurrentAllocationId = claim.AllocationId;
                    return accepted.WithSessionClaim(SessionClaimOutcome.Claimed);

                case SessionClaimOutcome.AllocatedMeanwhile:
                {
                    // The platform allocated this game server first: the game decides whether the joiner may join that session.
                    facts.SessionClaim = SessionClaimOutcome.AllocatedMeanwhile;
                    facts.CurrentAllocationId = claim.AllocationId;
                    AdmissionGateResult verdict;
                    lock (decideGate)
                    {
                        verdict = AskGate(ticket, facts);
                    }

                    return verdict.Admitted
                        ? accepted.WithSessionClaim(SessionClaimOutcome.AllocatedMeanwhile)
                        : Refuse(connectionId, accepted, verdict.Reason,
                            verdict.Detail ?? "the game refused to join the session the platform allocated meanwhile", SessionClaimOutcome.AllocatedMeanwhile);
                }

                default:
                    return Refuse(connectionId, accepted, JoinRejectReason.RefusedByGame,
                        "the game server could not claim a session (" + (claim.Detail ?? "no detail") + ")", SessionClaimOutcome.Failed);
            }
        }

        private AdmissionDecision Refuse(ulong connectionId, AdmissionDecision accepted, JoinRejectReason reason, string detail, SessionClaimOutcome claim)
        {
            Ledger.Release(connectionId);
            return accepted.WithSessionClaim(claim).AsRejection(reason, detail);
        }

        private static async Task<ClaimAttempt> ClaimSafely(ISessionClaimEvidence claimer, JoinTicket ticket, AdmissionFacts facts, CancellationToken cancellationToken)
        {
            try
            {
                return new ClaimAttempt(await claimer.ClaimSessionAsync(ticket, facts, cancellationToken), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ClaimAttempt(SessionClaimResult.Failed("cancelled"), null);
            }
            catch (Exception e)
            {
                return new ClaimAttempt(default, e);
            }
        }

        private AdmissionGateResult AskGate(JoinTicket ticket, AdmissionFacts facts)
        {
            try
            {
                return gate.CanAdmit(ticket, facts);
            }
            catch (Exception e)
            {
                return AdmissionGateResult.Reject(JoinRejectReason.RefusedByGame, "the admission gate failed (" + e.GetType().Name + ")");
            }
        }

        private AdmissionFacts NewFacts()
        {
            return new AdmissionFacts
            {
                ProtocolVersion = options.ProtocolVersion,
                Mode = options.Mode,
                LanOnly = options.LanOnly,
                MaxPlayers = options.MaxPlayers,
                AllowSelfAllocatedJoins = options.AllowSelfAllocatedJoins,
                ClaimIdleSessions = options.ClaimIdleSessions,
                Stopping = IsStopping,
                EvidenceSource = evidence.Source,
            };
        }

        private async Task<Exception> GatherSafely(JoinTicket ticket, AdmissionFacts facts, CancellationToken cancellationToken)
        {
            try
            {
                await evidence.GatherAsync(ticket, facts, options, cancellationToken);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        private AdmissionDecision Finish(AdmissionDecision decision, ulong connectionId, DateTimeOffset started)
        {
            long elapsed = (long)Math.Max(0, (scheduler.UtcNow - started).TotalMilliseconds);
            return decision.For(connectionId, elapsed);
        }
    }

    /// <summary>A session claim's answer, or the exception it threw.</summary>
    internal readonly struct ClaimAttempt
    {
        public ClaimAttempt(SessionClaimResult answer, Exception failure)
        {
            Answer = answer;
            Failure = failure;
        }

        public SessionClaimResult Answer { get; }

        public Exception Failure { get; }
    }
}
