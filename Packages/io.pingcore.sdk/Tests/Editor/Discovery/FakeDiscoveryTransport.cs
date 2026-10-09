using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Core.Discovery;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// A scripted Discovery behind <see cref="IHttpTransport"/>: responses are queued per method and
    /// path pattern and consumed in order (the last one repeats once the queue is down to it), every
    /// request is recorded with its headers, body and the virtual time it was sent, and a step can be
    /// an HTTP answer, a transport failure, or a gate the test opens. An unscripted request answers
    /// 599 and is listed in <see cref="Unscripted"/>, so a test can assert nothing unexpected was sent.
    /// Public so the Host suite can reuse it.
    /// </summary>
    public sealed class FakeDiscoveryTransport : IHttpTransport
    {
        /// <summary>The base URL tests give the client.</summary>
        public const string BaseUrl = "https://discovery.test";

        private readonly object gate = new object();
        private readonly List<Route> routes = new List<Route>();
        private readonly List<RecordedRequest> requests = new List<RecordedRequest>();
        private readonly IScheduler clock;

        /// <summary>Creates the fake; <paramref name="clock"/> stamps each request.</summary>
        public FakeDiscoveryTransport(IScheduler clock)
        {
            this.clock = clock;
        }

        /// <summary>Every request, in order.</summary>
        public List<RecordedRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return requests.ToList();
                }
            }
        }

        /// <summary>Requests no script matched.</summary>
        public List<RecordedRequest> Unscripted => Requests.Where(r => r.Unscripted).ToList();

        /// <summary>Queues <paramref name="steps"/> for <paramref name="method"/> and a path regex (anchored, no query).</summary>
        public FakeDiscoveryTransport On(string method, string pathPattern, params Step[] steps)
        {
            lock (gate)
            {
                Route route = routes.FirstOrDefault(r => r.Method == method && r.Pattern.ToString() == "^" + pathPattern + "$");
                if (route == null)
                {
                    route = new Route(method, new Regex("^" + pathPattern + "$"));
                    routes.Add(route);
                }

                foreach (Step step in steps)
                {
                    route.Steps.Enqueue(step);
                }
            }

            return this;
        }

        /// <summary>Requests to <paramref name="method"/> and the path regex.</summary>
        public List<RecordedRequest> To(string method, string pathPattern)
        {
            var regex = new Regex("^" + pathPattern + "$");
            return Requests.Where(r => r.Method == method && regex.IsMatch(r.Path)).ToList();
        }

        /// <summary>How many requests went to <paramref name="method"/> and the path regex.</summary>
        public int Count(string method, string pathPattern) => To(method, pathPattern).Count;

        /// <inheritdoc />
        public async Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            var uri = new Uri(request.Url);
            Step step;
            RecordedRequest record;
            lock (gate)
            {
                Route route = routes.FirstOrDefault(r => r.Method == request.Method && r.Pattern.IsMatch(uri.AbsolutePath));
                step = route == null ? null : route.Steps.Count > 1 ? route.Steps.Dequeue() : route.Steps.Count == 1 ? route.Steps.Peek() : null;
                record = new RecordedRequest(request.Method, uri.AbsolutePath, uri.Query.TrimStart('?'), new Dictionary<string, string>(request.Headers.ToDictionary(h => h.Key, h => h.Value), StringComparer.OrdinalIgnoreCase), request.Body, clock.UtcNow, step == null);
                requests.Add(record);
            }

            if (step == null)
            {
                return new PingCoreHttpResponse(599, null, "{\"error\":true,\"message\":\"unscripted request\"}");
            }

            if (step.Gate != null)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(step.Gate.Task, cancelled.Task) != step.Gate.Task)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
            }

            if (step.Failure != null)
            {
                throw step.Failure;
            }

            return new PingCoreHttpResponse(step.Status, step.Headers, step.Body);
        }

        /// <summary>One scripted answer.</summary>
        public sealed class Step
        {
            private Step(int status, string body, Dictionary<string, string> headers, Exception failure, TaskCompletionSource<bool> gate)
            {
                Status = status;
                Body = body;
                Headers = headers ?? new Dictionary<string, string>();
                Failure = failure;
                Gate = gate;
            }

            /// <summary>HTTP status.</summary>
            public int Status { get; }

            /// <summary>Body text.</summary>
            public string Body { get; }

            /// <summary>Response headers.</summary>
            public Dictionary<string, string> Headers { get; }

            /// <summary>Thrown instead of answering.</summary>
            public Exception Failure { get; }

            /// <summary>When set, the answer waits until the test completes it.</summary>
            public TaskCompletionSource<bool> Gate { get; }

            /// <summary>An answer with a JSON body.</summary>
            public static Step Json(int status, object body, params (string Name, string Value)[] headers)
            {
                string text = body is string s ? s : JToken.FromObject(body).ToString(Newtonsoft.Json.Formatting.None);
                return new Step(status, text, headers.ToDictionary(h => h.Name, h => h.Value), null, null);
            }

            /// <summary>A Discovery error body.</summary>
            public static Step Error(int status, string message, string reason = null, params (string Name, string Value)[] headers)
            {
                var body = new JObject { ["error"] = true, ["message"] = message };
                if (reason != null)
                {
                    body["reason"] = reason;
                }

                return new Step(status, body.ToString(Newtonsoft.Json.Formatting.None), headers.ToDictionary(h => h.Name, h => h.Value), null, null);
            }

            /// <summary>No answer: the transport throws.</summary>
            public static Step Unreachable(string message = "connection refused") => new Step(0, null, null, new PingCoreTransportException(message), null);

            /// <summary>This answer, held until <paramref name="gate"/> completes.</summary>
            public Step HeldBy(TaskCompletionSource<bool> gate) => new Step(Status, Body, Headers, Failure, gate);
        }

        /// <summary>One recorded request. <see cref="Authorization"/> is for assertions only; never print it.</summary>
        public sealed class RecordedRequest
        {
            internal RecordedRequest(string method, string path, string query, Dictionary<string, string> headers, string body, DateTimeOffset at, bool unscripted)
            {
                Method = method;
                Path = path;
                Query = query;
                Headers = headers;
                Body = body;
                At = at;
                Unscripted = unscripted;
            }

            /// <summary>HTTP method.</summary>
            public string Method { get; }

            /// <summary>Escaped path, no query.</summary>
            public string Path { get; }

            /// <summary>The raw query string without <c>?</c>.</summary>
            public string Query { get; }

            /// <summary>Request headers.</summary>
            public Dictionary<string, string> Headers { get; }

            /// <summary>Request body, or null.</summary>
            public string Body { get; }

            /// <summary>Virtual time sent.</summary>
            public DateTimeOffset At { get; }

            /// <summary>True when no script matched.</summary>
            public bool Unscripted { get; }

            /// <summary>The Authorization header, or null.</summary>
            public string Authorization => Headers.TryGetValue("Authorization", out string value) ? value : null;

            /// <summary>The body parsed, or null.</summary>
            public JObject Json => Body == null ? null : JObject.Parse(Body);

            /// <inheritdoc />
            public override string ToString() => $"{Method} {Path}";
        }

        private sealed class Route
        {
            public Route(string method, Regex pattern)
            {
                Method = method;
                Pattern = pattern;
            }

            public string Method { get; }

            public Regex Pattern { get; }

            public Queue<Step> Steps { get; } = new Queue<Step>();
        }
    }
}
