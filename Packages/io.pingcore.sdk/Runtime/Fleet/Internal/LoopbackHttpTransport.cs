using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;

namespace PingCore.Fleet
{
    /// <summary>
    /// The default transport: <c>HttpClient</c> to the loopback local SDK endpoint, never through
    /// a proxy. Unary calls use <see cref="FleetSdkOptions.CallTimeout"/> and send <c>Connection: close</c>:
    /// the supervisor (Node) closes an idle keep-alive socket after five seconds, so a pooled connection
    /// reused after a longer pause can be reset by a healthy endpoint; a fresh loopback connection per
    /// call costs next to nothing. The watch stream has no timeout and is read as it arrives
    /// (<see cref="HttpCompletionOption.ResponseHeadersRead"/>).
    /// Awaits do not leave the caller's context, so lines and results arrive on the Unity main
    /// thread when the call started there.
    /// </summary>
    internal sealed class LoopbackHttpTransport : IHttpTransport, ILineStreamTransport, IDisposable
    {
        /// <summary>Largest unary response body read.</summary>
        private const int MaxResponseBytes = 4 * 1024 * 1024;

        private readonly HttpClient unary;
        private readonly HttpClient stream;
        private readonly int maxLineBytes;

        public LoopbackHttpTransport(TimeSpan callTimeout, int maxLineBytes = WatchLineSplitter.DefaultMaxLineBytes)
        {
            unary = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = callTimeout > TimeSpan.Zero ? callTimeout : TimeSpan.FromSeconds(5),
                MaxResponseContentBufferSize = MaxResponseBytes,
            };
            stream = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            this.maxLineBytes = maxLineBytes;
        }

        public async Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            using (var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
            {
                if (request.Body != null)
                {
                    message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
                }

                foreach (KeyValuePair<string, string> header in request.Headers)
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                // Never reuse a pooled connection the endpoint may have closed as idle (see the class summary).
                message.Headers.ConnectionClose = true;

                using (HttpResponseMessage response = await unary.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken))
                {
                    string body = response.Content != null ? await response.Content.ReadAsStringAsync() : null;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
                    {
                        headers[header.Key] = string.Join(",", header.Value);
                    }

                    if (response.Content != null)
                    {
                        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
                        {
                            headers[header.Key] = string.Join(",", header.Value);
                        }
                    }

                    return new PingCoreHttpResponse((int)response.StatusCode, headers, string.IsNullOrEmpty(body) ? null : body);
                }
            }
        }

        public async Task<LineStreamResult> ReadLinesAsync(string url, Action<string> onLine, CancellationToken cancellationToken)
        {
            using (var message = new HttpRequestMessage(HttpMethod.Get, url))
            using (HttpResponseMessage response = await stream.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                int status = (int)response.StatusCode;
                if (status < 200 || status > 299)
                {
                    return new LineStreamResult(status, LineStreamEnd.NotSuccess);
                }

                using (Stream body = await response.Content.ReadAsStreamAsync())
                using (cancellationToken.Register(() => body.Dispose()))
                {
                    // Not every runtime's network stream observes the token in ReadAsync, so
                    // cancellation also disposes the stream, which ends a pending read.
                    var splitter = new WatchLineSplitter(maxLineBytes);
                    var buffer = new byte[16 * 1024];
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        }
                        catch (Exception) when (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        if (read == 0)
                        {
                            splitter.Finish(onLine);
                            return new LineStreamResult(status, LineStreamEnd.EndOfStream);
                        }

                        if (!splitter.Push(buffer, 0, read, onLine))
                        {
                            return new LineStreamResult(status, LineStreamEnd.LineTooLong);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }
        }

        public void Dispose()
        {
            unary.Dispose();
            stream.Dispose();
        }
    }
}
