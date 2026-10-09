using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The default beacon transport: <c>System.Net.WebSockets.ClientWebSocket</c>. No Unity package
    /// offers a general WebSocket client (Unity Transport's WebSocket is a netcode transport) and
    /// the SDK ships no external DLL. Not available on WebGL, where <see cref="LatencyProbe.CreateDefaultTransport"/>
    /// picks the browser's WebSocket instead (<c>Latency/WebGL/</c>).
    /// Server ping frames are answered by the socket itself; binary messages are skipped; a close
    /// is echoed and then fails the location. Each received message carries the time its last read
    /// completed, read on the completing thread (<see cref="EchoMessage.ReceivedTimestamp"/>).
    /// </summary>
    public sealed class ClientWebSocketEchoTransport : IWebSocketEchoTransport
    {
        /// <summary>Largest text message read; a beacon answers four bytes.</summary>
        private const int MaxMessageBytes = 64 * 1024;

        /// <inheritdoc />
        public async Task<IWebSocketEchoSession> ConnectAsync(string url, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != "wss" && uri.Scheme != "ws"))
            {
                throw new ArgumentException("a beacon URL is an absolute ws or wss URL", nameof(url));
            }

            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return new Session(socket);
        }

        private sealed class Session : IWebSocketEchoSession
        {
            private readonly ClientWebSocket socket;
            private readonly byte[] buffer = new byte[1024];

            public Session(ClientWebSocket socket)
            {
                this.socket = socket;
            }

            public Task SendTextAsync(string text, CancellationToken cancellationToken)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }

            /// <summary>
            /// Reads without <c>await</c>: each socket read gets an
            /// <see cref="TaskContinuationOptions.ExecuteSynchronously"/> continuation on
            /// <see cref="TaskScheduler.Default"/>, which reads the <see cref="Stopwatch"/> first, on the
            /// thread that completed the read, and then reads on or completes the message there. An
            /// <c>await</c> here would resume on the Unity main thread up to a frame late and the
            /// stamp with it (every median a multiple of the frame time).
            /// </summary>
            public Task<EchoMessage> ReceiveTextAsync(CancellationToken cancellationToken)
            {
                var receive = new Receive(this, cancellationToken);
                receive.ReadNext();
                return receive.Completion.Task;
            }

            private void EchoCloseAndForget(CancellationToken cancellationToken)
            {
                try
                {
                    socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken)
                        .ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception)
                {
                    // The location fails either way; echoing the close is a courtesy.
                }
            }

            /// <summary>One message being assembled from one or more socket reads.</summary>
            private sealed class Receive
            {
                private readonly Session session;
                private readonly CancellationToken cancellationToken;
                private readonly MemoryStream message = new MemoryStream();

                public Receive(Session session, CancellationToken cancellationToken)
                {
                    this.session = session;
                    this.cancellationToken = cancellationToken;
                }

                public TaskCompletionSource<EchoMessage> Completion { get; } = new TaskCompletionSource<EchoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

                public void ReadNext()
                {
                    Task<WebSocketReceiveResult> read;
                    try
                    {
                        read = session.socket.ReceiveAsync(new ArraySegment<byte>(session.buffer), cancellationToken);
                    }
                    catch (Exception e)
                    {
                        Fail(e);
                        return;
                    }

                    read.ContinueWith(OnRead, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                private void OnRead(Task<WebSocketReceiveResult> read)
                {
                    long stamp = Stopwatch.GetTimestamp();
                    try
                    {
                        if (read.IsCanceled)
                        {
                            message.Dispose();
                            Completion.TrySetCanceled(cancellationToken);
                            return;
                        }

                        if (read.IsFaulted)
                        {
                            Fail(read.Exception.InnerException ?? read.Exception);
                            return;
                        }

                        WebSocketReceiveResult result = read.GetAwaiter().GetResult();
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            session.EchoCloseAndForget(cancellationToken);
                            Fail(new WebSocketException("the beacon closed the connection"));
                            return;
                        }

                        message.Write(session.buffer, 0, result.Count);
                        if (message.Length > MaxMessageBytes)
                        {
                            Fail(new WebSocketException("the beacon sent an oversized message"));
                            return;
                        }

                        if (!result.EndOfMessage)
                        {
                            ReadNext();
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                            message.Dispose();
                            Completion.TrySetResult(new EchoMessage(text, stamp));
                            return;
                        }

                        message.SetLength(0);
                        ReadNext();
                    }
                    catch (Exception e)
                    {
                        Fail(e);
                    }
                }

                private void Fail(Exception e)
                {
                    message.Dispose();
                    Completion.TrySetException(e);
                }
            }

            public void Dispose()
            {
                try
                {
                    socket.Abort();
                }
                catch (Exception)
                {
                    // Already closed.
                }

                socket.Dispose();
            }
        }
    }
}
