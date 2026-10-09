#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOT;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The beacon transport of a WebGL player, where <c>ClientWebSocket</c> does not exist: the browser's
    /// <c>WebSocket</c> through <c>PingCoreLatency.jslib</c>. <see cref="LatencyProbe"/> picks it on WebGL
    /// (<see cref="LatencyProbe.CreateDefaultTransport"/>), so the probe's procedure (warm-up, median of 5, a failed
    /// location left out, never 0) is the same C# as everywhere else. The browser runs one thread: the plug-in calls back
    /// into C# from <c>onopen</c>, <c>onmessage</c> and <c>onclose</c>, each message carrying its age since the browser
    /// created its message event (<c>event.timeStamp</c>), and <see cref="WebGlEchoClock"/> turns that into the arrival's
    /// <see cref="Stopwatch"/> timestamp. Tasks complete with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>,
    /// so no game code runs inside a browser event. Binary messages are skipped; a close fails the location.
    /// <para>On WebGL the SDK cannot read Discovery's <c>Retry-After</c> header (Discovery does not expose it to
    /// cross-origin pages), so after a 429 the Discovery calls fall back to their fixed backoff; that is the Discovery
    /// caller's concern, not this transport's.</para>
    /// </summary>
    internal sealed class WebGlEchoTransport : IWebSocketEchoTransport
    {
        private const int MaxMessageBytes = 64 * 1024;

        private static readonly Dictionary<int, Session> Sessions = new Dictionary<int, Session>();

        // Held for the life of the player: the plug-in keeps calling these function pointers.
        private static readonly OpenCallback OpenHandler = OnOpen;
        private static readonly MessageCallback MessageHandler = OnMessage;
        private static readonly CloseCallback CloseHandler = OnClose;
        private static bool initialised;

        private delegate void OpenCallback(int socket);

        private delegate void MessageCallback(int socket, IntPtr text, int bytes, int ageMicros);

        private delegate void CloseCallback(int socket, int code);

        /// <inheritdoc />
        public Task<IWebSocketEchoSession> ConnectAsync(string url, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != "wss" && uri.Scheme != "ws"))
            {
                throw new ArgumentException("a beacon URL is an absolute ws or wss URL", nameof(url));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<IWebSocketEchoSession>(cancellationToken);
            }

            if (!initialised)
            {
                PingCoreLatency_Init(OpenHandler, MessageHandler, CloseHandler);
                initialised = true;
            }

            int id = PingCoreLatency_Connect(uri.AbsoluteUri);
            if (id <= 0)
            {
                return Task.FromException<IWebSocketEchoSession>(new WebSocketException("the browser refused the beacon URL"));
            }

            var session = new Session(id);
            Sessions[id] = session;
            session.CancelConnectOn(cancellationToken);
            return session.Opened.Task;
        }

        [DllImport("__Internal")]
        private static extern void PingCoreLatency_Init(OpenCallback onOpen, MessageCallback onMessage, CloseCallback onClose);

        [DllImport("__Internal")]
        private static extern int PingCoreLatency_Connect(string url);

        [DllImport("__Internal")]
        private static extern int PingCoreLatency_Send(int socket, string text);

        [DllImport("__Internal")]
        private static extern void PingCoreLatency_Close(int socket);

        [MonoPInvokeCallback(typeof(OpenCallback))]
        private static void OnOpen(int socket)
        {
            if (Sessions.TryGetValue(socket, out Session session))
            {
                session.Open();
            }
        }

        [MonoPInvokeCallback(typeof(MessageCallback))]
        private static void OnMessage(int socket, IntPtr text, int bytes, int ageMicros)
        {
            // The C# clock first; the plug-in's age covers the time since the browser created the message event.
            long now = Stopwatch.GetTimestamp();
            if (!Sessions.TryGetValue(socket, out Session session))
            {
                return;
            }

            if (bytes < 0 || bytes > MaxMessageBytes || text == IntPtr.Zero)
            {
                session.Fail(new WebSocketException("the beacon sent an unreadable message"));
                return;
            }

            var copy = new byte[bytes];
            Marshal.Copy(text, copy, 0, bytes);
            session.Arrived(new EchoMessage(Encoding.UTF8.GetString(copy), WebGlEchoClock.ArrivalTimestamp(now, ageMicros, Stopwatch.Frequency)));
        }

        [MonoPInvokeCallback(typeof(CloseCallback))]
        private static void OnClose(int socket, int code)
        {
            if (Sessions.TryGetValue(socket, out Session session))
            {
                Sessions.Remove(socket);
                session.Fail(new WebSocketException("the beacon closed the connection (" + code + ")"));
            }
        }

        private sealed class Session : IWebSocketEchoSession
        {
            private readonly int id;
            private readonly Queue<EchoMessage> arrived = new Queue<EchoMessage>();
            private TaskCompletionSource<EchoMessage> pending;
            private CancellationTokenRegistration pendingCancel;
            private CancellationTokenRegistration connectCancel;
            private Exception failure;
            private bool disposed;

            public Session(int id)
            {
                this.id = id;
            }

            public TaskCompletionSource<IWebSocketEchoSession> Opened { get; } =
                new TaskCompletionSource<IWebSocketEchoSession>(TaskCreationOptions.RunContinuationsAsynchronously);

            public void CancelConnectOn(CancellationToken cancellationToken)
            {
                if (cancellationToken.CanBeCanceled)
                {
                    connectCancel = cancellationToken.Register(() =>
                    {
                        if (Opened.TrySetCanceled(cancellationToken))
                        {
                            Dispose();
                        }
                    });
                }
            }

            public void Open()
            {
                connectCancel.Dispose();
                Opened.TrySetResult(this);
            }

            public void Arrived(EchoMessage message)
            {
                TaskCompletionSource<EchoMessage> waiting = pending;
                if (waiting != null)
                {
                    pending = null;
                    pendingCancel.Dispose();
                    waiting.TrySetResult(message);
                    return;
                }

                // A beacon answers one pong per ping, so a queue only holds the odd unsolicited message; keep it small.
                if (arrived.Count >= 16)
                {
                    arrived.Dequeue();
                }

                arrived.Enqueue(message);
            }

            public void Fail(Exception error)
            {
                failure = failure ?? error;
                connectCancel.Dispose();
                if (Opened.TrySetException(failure))
                {
                    Dispose();
                }

                TaskCompletionSource<EchoMessage> waiting = pending;
                pending = null;
                pendingCancel.Dispose();
                waiting?.TrySetException(failure);
            }

            public Task SendTextAsync(string text, CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled(cancellationToken);
                }

                if (failure != null || disposed || PingCoreLatency_Send(id, text) != 1)
                {
                    return Task.FromException(failure ?? new WebSocketException("the beacon connection is not open"));
                }

                return Task.CompletedTask;
            }

            public Task<EchoMessage> ReceiveTextAsync(CancellationToken cancellationToken)
            {
                if (arrived.Count > 0)
                {
                    return Task.FromResult(arrived.Dequeue());
                }

                if (failure != null || disposed)
                {
                    return Task.FromException<EchoMessage>(failure ?? new ObjectDisposedException(nameof(WebGlEchoTransport)));
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled<EchoMessage>(cancellationToken);
                }

                if (pending != null)
                {
                    return Task.FromException<EchoMessage>(new InvalidOperationException("one receive at a time"));
                }

                var waiting = new TaskCompletionSource<EchoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = waiting;
                if (cancellationToken.CanBeCanceled)
                {
                    pendingCancel = cancellationToken.Register(() =>
                    {
                        if (pending == waiting)
                        {
                            pending = null;
                        }

                        waiting.TrySetCanceled(cancellationToken);
                    });
                }

                return waiting.Task;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                Sessions.Remove(id);
                PingCoreLatency_Close(id);
                connectCancel.Dispose();
                Opened.TrySetCanceled();
                TaskCompletionSource<EchoMessage> waiting = pending;
                pending = null;
                pendingCancel.Dispose();
                waiting?.TrySetCanceled();
            }
        }
    }
}
#endif
