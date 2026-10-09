using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using UnityEngine;
using UnityEngine.Networking;

namespace PingCore.Unity
{
    /// <summary>
    /// <see cref="IHttpTransport"/> on <c>UnityWebRequest</c>, the transport the Discovery client
    /// and host use on every platform (WebGL and mobile included). Call it from the Unity main
    /// thread: it polls the request once per frame with <c>Awaitable.NextFrameAsync</c>, so the
    /// answer arrives on the main thread. Every HTTP status comes back as a response with its body
    /// and headers; it throws <see cref="PingCoreTransportException"/> only when no answer arrived
    /// (its message is Unity's error text, never the URL) and <see cref="OperationCanceledException"/>
    /// on cancellation, which aborts the request. Redirects are not followed, so a bearer is never
    /// forwarded to another host.
    /// </summary>
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        private readonly int timeoutSeconds;

        /// <summary>Creates the transport.</summary>
        /// <param name="callTimeout">Per-call timeout, rounded up to whole seconds (UnityWebRequest's unit); at least 1 s.</param>
        public UnityWebRequestTransport(TimeSpan callTimeout)
        {
            timeoutSeconds = Math.Max(1, (int)Math.Ceiling(callTimeout.TotalSeconds));
        }

        /// <summary>The per-call timeout in seconds.</summary>
        public int TimeoutSeconds => timeoutSeconds;

        /// <inheritdoc />
        public async Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var web = new UnityWebRequest(request.Url, request.Method))
            {
                web.downloadHandler = new DownloadHandlerBuffer();
                if (request.Body != null)
                {
                    web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body));
                    web.SetRequestHeader("Content-Type", "application/json");
                }

                foreach (KeyValuePair<string, string> header in request.Headers)
                {
                    web.SetRequestHeader(header.Key, header.Value);
                }

                web.timeout = timeoutSeconds;
                web.redirectLimit = 0;

                UnityWebRequestAsyncOperation operation = web.SendWebRequest();
                try
                {
                    while (!operation.isDone)
                    {
                        await Awaitable.NextFrameAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    web.Abort();
                    throw;
                }

                if (web.result == UnityWebRequest.Result.ConnectionError || web.result == UnityWebRequest.Result.DataProcessingError)
                {
                    throw new PingCoreTransportException(string.IsNullOrEmpty(web.error) ? web.result.ToString() : web.error);
                }

                int status = (int)web.responseCode;
                if (status <= 0)
                {
                    throw new PingCoreTransportException("no HTTP status");
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> received = web.GetResponseHeaders();
                if (received != null)
                {
                    foreach (KeyValuePair<string, string> header in received)
                    {
                        headers[header.Key] = header.Value;
                    }
                }

                string body = web.downloadHandler != null ? web.downloadHandler.text : null;
                return new PingCoreHttpResponse(status, headers, string.IsNullOrEmpty(body) ? null : body);
            }
        }
    }
}
