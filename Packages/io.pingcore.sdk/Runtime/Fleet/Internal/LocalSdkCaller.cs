using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Fleet
{
    /// <summary>One answer from the local SDK endpoint, classified.</summary>
    internal sealed class LocalSdkAnswer
    {
        public LocalSdkAnswer(FleetCallOutcome outcome, int status, string body, string message)
        {
            Outcome = outcome;
            Status = status;
            Body = body;
            Message = message;
        }

        public FleetCallOutcome Outcome { get; }

        public int Status { get; }

        public string Body { get; }

        public string Message { get; }

        public FleetCallResult ToResult() => new FleetCallResult(Outcome, Status, Message);
    }

    /// <summary>
    /// Sends one unary request and classifies it. HTTP status classification is pure
    /// (<see cref="Classify"/>): 2xx Ok; the 501 fallback, or a 404 that is not JSON, Unsupported;
    /// anything else Rejected, with the body's <c>message</c> (or <c>error</c>). A transport
    /// failure is classified by <see cref="TransportFailure"/>: the caller remembers whether the
    /// endpoint ever answered, and the shim maps that and the failure kind to an outcome.
    /// </summary>
    internal sealed class LocalSdkCaller
    {
        private readonly IHttpTransport transport;
        private readonly string baseUrl;
        private readonly Func<bool, TransportFailureKind, FleetCallOutcome> failureOutcome;
        private readonly CancellationToken lifetime;
        private int answered;

        /// <param name="failureOutcome">Maps (the endpoint answered before, the failure kind) to the outcome of a transport failure.</param>
        public LocalSdkCaller(IHttpTransport transport, string baseUrl, Func<bool, TransportFailureKind, FleetCallOutcome> failureOutcome, CancellationToken lifetime)
        {
            this.transport = transport;
            this.baseUrl = baseUrl;
            this.failureOutcome = failureOutcome;
            this.lifetime = lifetime;
        }

        public string BaseUrl => baseUrl;

        /// <summary>True once the endpoint has answered any request (any HTTP status) or sent a watch line.</summary>
        public bool HasAnswered => Volatile.Read(ref answered) == 1;

        /// <summary>Records that the endpoint answered; the watch reader calls it on a line.</summary>
        public void MarkAnswered() => Volatile.Write(ref answered, 1);

        public async Task<LocalSdkAnswer> SendAsync(string method, string path, string body, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
            {
                return new LocalSdkAnswer(FleetCallOutcome.Cancelled, 0, null, "cancelled");
            }

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime))
            {
                PingCoreHttpResponse response;
                try
                {
                    response = await transport.SendAsync(new PingCoreHttpRequest(method, baseUrl + path, null, body), linked.Token);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                {
                    return new LocalSdkAnswer(FleetCallOutcome.Cancelled, 0, null, "cancelled");
                }
                catch (Exception e)
                {
                    // A timeout surfaces as a cancellation the caller did not ask for; it is a
                    // transport failure of kind Other, never EndpointClosed on its own.
                    FleetCallOutcome outcome = failureOutcome(HasAnswered, TransportFailure.Classify(e));
                    return new LocalSdkAnswer(outcome, 0, null, Describe(e));
                }

                MarkAnswered();

                (FleetCallOutcome classified, string message) = Classify(response.Status, response.Body);
                return new LocalSdkAnswer(classified, response.Status, response.Body, message);
            }
        }

        /// <summary>Classifies an HTTP answer; pure.</summary>
        public static (FleetCallOutcome Outcome, string Message) Classify(int status, string body)
        {
            if (status >= 200 && status <= 299)
            {
                return (FleetCallOutcome.Ok, null);
            }

            JObject json = LocalSdkValues.TryParseObject(body);
            string message = StringField(json, "message") ?? StringField(json, "error");
            if (status == 501)
            {
                return (FleetCallOutcome.Unsupported, message ?? "not implemented by this supervisor");
            }

            if (status == 404 && json == null)
            {
                return (FleetCallOutcome.Unsupported, "a non-JSON 404: this supervisor has no such route");
            }

            return (FleetCallOutcome.Rejected, message ?? "HTTP " + status);
        }

        private static string StringField(JObject json, string name)
        {
            return json?[name] is JValue value && value.Type == JTokenType.String ? (string)value : null;
        }

        private static string Describe(Exception e)
        {
            Exception inner = e;
            while (inner.InnerException != null)
            {
                inner = inner.InnerException;
            }

            return inner == e ? "transport failure (" + e.GetType().Name + ")" : "transport failure (" + e.GetType().Name + ", " + inner.GetType().Name + ")";
        }
    }
}
