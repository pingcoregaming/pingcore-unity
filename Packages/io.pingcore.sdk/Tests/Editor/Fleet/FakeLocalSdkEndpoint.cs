using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// An in-process stand-in for the supervisor's local SDK endpoint, on
    /// <c>http://127.0.0.1:&lt;ephemeral&gt;/</c> over raw TCP (<see cref="FakeHttpConnection"/>). It models
    /// the supervisor 1.3.4 routes, its reservation routes and its compatibility fallback:
    /// <list type="bullet">
    /// <item>Every routed write integrates and no GET does; the JSON 501 fallback for an unknown route does
    /// not integrate (the supervisor answers it without marking the game integrated).</item>
    /// <item>A change writes its watch frame BEFORE the answer, as the supervisor emits the GameServer frame
    /// synchronously inside the route (<see cref="FrameBeforeAnswer"/>, on by default).</item>
    /// <item><see cref="CloseEndpoint"/> is Node's <c>server.close()</c>: new connections are refused while the
    /// open watch streams stay open. <see cref="DropWatchOnly"/> ends the watch streams and keeps listening.</item>
    /// </list>
    /// The routes are in <c>FakeLocalSdkEndpoint.Routes.cs</c>, the GameServer view in
    /// <c>FakeLocalSdkEndpoint.View.cs</c> and the watch streams in <c>FakeLocalSdkEndpoint.Watch.cs</c>.
    /// </summary>
    internal sealed partial class FakeLocalSdkEndpoint : IDisposable
    {
        private readonly object gate = new object();
        private readonly TcpListener listener;
        private readonly List<FakeRequest> requests = new List<FakeRequest>();
        private readonly Dictionary<string, Queue<(int Status, string Body)>> failures = new Dictionary<string, Queue<(int, string)>>(StringComparer.Ordinal);
        private readonly HashSet<string> abortAnswers = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> heldFrames = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action> beforeRoute = new Dictionary<string, Action>(StringComparer.Ordinal);
        private bool listening;

        private FakeLocalSdkEndpoint(TcpListener listener)
        {
            this.listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listening = true;
        }

        public int Port { get; }

        public bool Integrated { get; private set; }

        public bool Ready { get; private set; }

        public bool ShutdownRequested { get; private set; }

        /// <summary>Write a change's watch frame before its answer (the supervisor's order). Default true.</summary>
        public bool FrameBeforeAnswer { get; set; } = true;

        /// <summary>With <see cref="FrameBeforeAnswer"/>, how long the answer waits after the frame, so a test can rely on the order.</summary>
        public TimeSpan AnswerDelayAfterFrame { get; set; } = TimeSpan.Zero;

        /// <summary>Answer every watch request with 503 instead of a stream (to fail reconnects).</summary>
        public bool RefuseWatch { get; set; }

        public JObject Joinable { get; private set; }

        public int WatchRequests { get; private set; }

        /// <summary>Starts the fake on an ephemeral loopback port.</summary>
        public static FakeLocalSdkEndpoint Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var fake = new FakeLocalSdkEndpoint(listener);
            Task.Run(fake.AcceptLoop);
            return fake;
        }

        public List<FakeRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return requests.ToList();
                }
            }
        }

        public int Count(string method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

        /// <summary>The next request to <paramref name="path"/> gets this status and raw body instead of the route.</summary>
        public void FailNext(string path, int status, string body)
        {
            lock (gate)
            {
                if (!failures.TryGetValue(path, out Queue<(int, string)> queue))
                {
                    queue = new Queue<(int, string)>();
                    failures[path] = queue;
                }

                queue.Enqueue((status, body));
            }
        }

        /// <summary>
        /// The next request to <paramref name="path"/> runs its route (the state changes) but its frame is
        /// not pushed and the connection is reset instead of answered: the answer is lost. Push the frame
        /// later with <see cref="PushView"/>.
        /// </summary>
        public void AbortAnswerNext(string path)
        {
            lock (gate)
            {
                abortAnswers.Add(path);
            }
        }

        /// <summary>
        /// The next request to <paramref name="path"/> runs its route and is answered, but its frame is not pushed (a
        /// watch stream that lost the frame). Push it later with <see cref="PushView"/>.
        /// </summary>
        public void HoldFrameNext(string path)
        {
            lock (gate)
            {
                heldFrames.Add(path);
            }
        }

        /// <summary>
        /// The next request to <paramref name="path"/> runs <paramref name="action"/> first, outside the fake's lock and before
        /// its route (for example a Discovery allocation frame that lands while the request is in flight).
        /// </summary>
        public void BeforeRouteNext(string path, Action action)
        {
            lock (gate)
            {
                beforeRoute[path] = action;
            }
        }

        /// <summary>
        /// The SIGTERM model, Node's <c>server.close()</c>: the listener stops, so new connections are
        /// refused, while every open watch stream stays open (it is a response that never ends).
        /// </summary>
        public void CloseEndpoint()
        {
            lock (gate)
            {
                if (!listening)
                {
                    return;
                }

                listening = false;
            }

            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }
        }

        /// <summary>Stops listening and ends every watch stream.</summary>
        public void Dispose()
        {
            CloseEndpoint();
            DropWatchOnly(true);
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => Handle(new FakeHttpConnection(client)));
            }
        }

        private void Handle(FakeHttpConnection connection)
        {
            try
            {
                Route(connection, connection.ReadRequest());
            }
            catch (Exception)
            {
                connection.End(true);
            }
        }

        private void Route(FakeHttpConnection connection, FakeRequest request)
        {
            (int Status, string Body)? failure = null;
            bool abortAnswer;
            bool holdFrame;
            Action before;
            lock (gate)
            {
                if (beforeRoute.TryGetValue(request.Path, out before))
                {
                    beforeRoute.Remove(request.Path);
                }
            }

            before?.Invoke();
            lock (gate)
            {
                requests.Add(request);
                if (failures.TryGetValue(request.Path, out Queue<(int, string)> queue) && queue.Count > 0)
                {
                    failure = queue.Dequeue();
                }

                abortAnswer = failure == null && abortAnswers.Remove(request.Path);
                holdFrame = failure == null && heldFrames.Remove(request.Path);
            }

            if (failure != null)
            {
                bool json = failure.Value.Body != null && failure.Value.Body.TrimStart().StartsWith("{", StringComparison.Ordinal);
                lock (gate)
                {
                    // A stand-in for the route's own answer; a 501 or a non-JSON 404 is "no such route", which never integrates.
                    if (request.IsWrite && failure.Value.Status != 501 && !(failure.Value.Status == 404 && !json))
                    {
                        Integrated = true;
                    }
                }

                connection.Respond(failure.Value.Status, failure.Value.Body, json ? "application/json; charset=utf-8" : "text/html; charset=utf-8");
                return;
            }

            string[] segments = request.Path.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
            bool changed = false;
            bool routed = true;
            int status = 200;
            JToken answer;
            lock (gate)
            {
                answer = Dispatch(request.Method, request.Path, segments, request.Body, ref status, ref changed, ref routed);
                if (request.IsWrite && routed)
                {
                    Integrated = true;
                }
            }

            if (answer == null)
            {
                OpenWatch(connection);
                return;
            }

            if (abortAnswer)
            {
                connection.End(true);
                return;
            }

            string text = answer.ToString(Formatting.None);
            if (holdFrame)
            {
                changed = false;
            }

            if (changed && FrameBeforeAnswer)
            {
                PushView();
                if (AnswerDelayAfterFrame > TimeSpan.Zero)
                {
                    Thread.Sleep(AnswerDelayAfterFrame);
                }
            }

            connection.Respond(status, text, "application/json; charset=utf-8");
            if (changed && !FrameBeforeAnswer)
            {
                PushView();
            }
        }
    }
}
