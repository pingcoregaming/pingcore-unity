using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>Counters, session end, joinable records, backfills and pushed reservations.</summary>
    public sealed partial class FleetSdk
    {
        /// <inheritdoc />
        public async Task<CounterResult> SetCounterAsync(string name, long count, CancellationToken cancellationToken)
        {
            RequireText(name, nameof(name));
            if (!IsHosted)
            {
                return new CounterResult(FleetCallOutcome.Inert, 0, "not a hosted game server", name, null, null);
            }

            string body = LocalSdkValues.Serialize(new CounterPatchRequest { Count = count });
            LocalSdkAnswer answer = await caller.SendAsync("PATCH", CounterPath(name), body, cancellationToken);
            LogAnswer("counter", answer, false);
            return ToCounterResult(name, answer);
        }

        /// <inheritdoc />
        public async Task<CounterResult> GetCounterAsync(string name, CancellationToken cancellationToken)
        {
            RequireText(name, nameof(name));
            if (!IsHosted)
            {
                return new CounterResult(FleetCallOutcome.Inert, 0, "not a hosted game server", name, null, null);
            }

            LocalSdkAnswer answer = await caller.SendAsync("GET", CounterPath(name), null, cancellationToken);
            LogAnswer("counter", answer, false);
            return ToCounterResult(name, answer);
        }

        /// <inheritdoc />
        public async Task<FleetCallResult> EndSessionAsync(string allocationId, CancellationToken cancellationToken)
        {
            RequireText(allocationId, nameof(allocationId));
            if (!IsHosted)
            {
                return InertResult();
            }

            // Marked before sending: the supervisor writes the frame that clears the allocation
            // before it answers, so the frame usually beats the answer.
            lock (gate)
            {
                allocations.MarkEnding(allocationId);
            }

            LocalSdkAnswer answer = await caller.SendAsync("POST", SessionPath(allocationId) + "/ended", "{}", cancellationToken);
            LogAnswer("sessionEnded", answer, true);
            lock (gate)
            {
                allocations.OnEndAnswered(allocationId, answer.Outcome);
            }

            return answer.ToResult();
        }

        /// <inheritdoc />
        public async Task<JoinablePublishResult> PublishJoinableAsync(string allocationId, JoinableSessionRequest request, CancellationToken cancellationToken)
        {
            RequireText(allocationId, nameof(allocationId));
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!IsHosted)
            {
                return new JoinablePublishResult(FleetCallOutcome.Inert, 0, "not a hosted game server", null);
            }

            LocalSdkAnswer answer = await caller.SendAsync("POST", SessionPath(allocationId) + "/joinable", LocalSdkValues.Serialize(request), cancellationToken);
            LogAnswer("joinable", answer, false);
            if (answer.Outcome != FleetCallOutcome.Ok)
            {
                return new JoinablePublishResult(answer.Outcome, answer.Status, answer.Message, null);
            }

            JoinableSessionRecord record = LocalSdkValues.TryDeserialize<JoinableSessionRecord>(answer.Body, out string problem);
            if (record == null)
            {
                Log(FleetLogLevel.Warning, "joinable", "the joinable echo is not a record (" + problem + ")", answer.Status, answer.Outcome);
            }

            return new JoinablePublishResult(answer.Outcome, answer.Status, null, record);
        }

        /// <inheritdoc />
        public async Task<FleetCallResult> WithdrawJoinableAsync(string allocationId, CancellationToken cancellationToken)
        {
            RequireText(allocationId, nameof(allocationId));
            if (!IsHosted)
            {
                return InertResult();
            }

            LocalSdkAnswer answer = await caller.SendAsync("DELETE", SessionPath(allocationId) + "/joinable", null, cancellationToken);
            LogAnswer("joinableWithdrawn", answer, false);
            return answer.ToResult();
        }

        /// <inheritdoc />
        public async Task<BackfillsResult> GetBackfillsAsync(CancellationToken cancellationToken)
        {
            if (!IsHosted)
            {
                return new BackfillsResult(FleetCallOutcome.Inert, 0, "not a hosted game server", null);
            }

            LocalSdkAnswer answer = await caller.SendAsync("GET", "/v1/backfills", null, cancellationToken);
            LogAnswer("backfills", answer, false);
            if (answer.Outcome != FleetCallOutcome.Ok)
            {
                return new BackfillsResult(answer.Outcome, answer.Status, answer.Message, null);
            }

            BackfillList list = LocalSdkValues.TryDeserialize<BackfillList>(answer.Body, out string problem);
            if (list == null)
            {
                Log(FleetLogLevel.Warning, "backfills", "the backfills body did not parse (" + problem + ")", answer.Status, answer.Outcome);
            }

            return new BackfillsResult(answer.Outcome, answer.Status, null, list?.Backfills);
        }

        /// <inheritdoc />
        public async Task<ReservationLookup> GetReservationAsync(string reservationId, CancellationToken cancellationToken)
        {
            RequireText(reservationId, nameof(reservationId));
            if (!IsHosted)
            {
                return new ReservationLookup(ReservationLookupStatus.Inert, null, 0, "not a hosted game server");
            }

            string path = "/pingcore/reservations/" + LocalSdkValues.Segment(reservationId);
            DateTimeOffset deadline = scheduler.UtcNow + options.ReservationWait;
            TimeSpan poll = options.ReservationPoll > TimeSpan.Zero ? options.ReservationPoll : TimeSpan.FromMilliseconds(250);
            while (true)
            {
                LocalSdkAnswer answer = await caller.SendAsync("GET", path, null, cancellationToken);
                ReservationVerdict verdict = ReservationLookupPolicy.Classify(answer.Outcome, answer.Status, answer.Body, reservationId, scheduler.UtcNow);
                // Look again until the deadline itself: the last wait is cut short so the final
                // lookup lands at the deadline, and the window is never shorter than ReservationWait.
                DateTimeOffset now = scheduler.UtcNow;
                if (verdict.Retry && now < deadline)
                {
                    TimeSpan wait = deadline - now < poll ? deadline - now : poll;
                    try
                    {
                        await scheduler.DelayAsync(wait, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return new ReservationLookup(ReservationLookupStatus.Cancelled, null, answer.Status, "cancelled");
                    }

                    continue;
                }

                if (verdict.Status == ReservationLookupStatus.Unreachable)
                {
                    LogAnswer("reservation", answer, false);
                }
                else if (verdict.Status == ReservationLookupStatus.Unsupported || verdict.Status == ReservationLookupStatus.Error)
                {
                    Log(FleetLogLevel.Warning, "reservation", verdict.Message, answer.Status, answer.Outcome);
                }

                return new ReservationLookup(verdict.Status, verdict.Reservation, answer.Status, verdict.Message);
            }
        }

        /// <inheritdoc />
        public async Task<ReservationsResult> ListReservationsAsync(CancellationToken cancellationToken)
        {
            if (!IsHosted)
            {
                return new ReservationsResult(FleetCallOutcome.Inert, 0, "not a hosted game server", null);
            }

            LocalSdkAnswer answer = await caller.SendAsync("GET", "/pingcore/reservations", null, cancellationToken);
            LogAnswer("reservations", answer, false);
            if (answer.Outcome != FleetCallOutcome.Ok)
            {
                return new ReservationsResult(answer.Outcome, answer.Status, answer.Message, null);
            }

            LocalReservationList list = LocalSdkValues.TryDeserialize<LocalReservationList>(answer.Body, out string problem);
            if (list == null)
            {
                Log(FleetLogLevel.Warning, "reservations", "the reservations body did not parse (" + problem + ")", answer.Status, answer.Outcome);
            }

            return new ReservationsResult(answer.Outcome, answer.Status, null, list?.Reservations ?? new List<LocalReservation>());
        }

        private CounterResult ToCounterResult(string name, LocalSdkAnswer answer)
        {
            if (answer.Outcome != FleetCallOutcome.Ok)
            {
                return new CounterResult(answer.Outcome, answer.Status, answer.Message, name, null, null);
            }

            CounterView view = LocalSdkValues.TryDeserialize<CounterView>(answer.Body, out string problem);
            if (view == null)
            {
                Log(FleetLogLevel.Warning, "counter", "the counter echo did not parse (" + problem + ")", answer.Status, answer.Outcome);
                return new CounterResult(answer.Outcome, answer.Status, null, name, null, null);
            }

            return new CounterResult(answer.Outcome, answer.Status, null, view.Name ?? name, LocalSdkValues.ParseInt64(view.Count), LocalSdkValues.ParseInt64(view.Capacity));
        }

        private static string CounterPath(string name) => "/v1beta1/counters/" + LocalSdkValues.Segment(name);

        private static string SessionPath(string allocationId) => "/v1/sessions/" + LocalSdkValues.Segment(allocationId);

        private static void RequireText(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(name + " must be a non-empty string.", name);
            }
        }
    }
}
