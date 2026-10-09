using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;

namespace PingCore.Editor.Workspace.Api
{
    /// <summary>
    /// The Editor's <see cref="IHttpTransport"/> for the PingCore API, on
    /// <see cref="HttpClient"/>: https only, redirects never followed (a redirect must never carry
    /// the bearer to another host), no cookies, and a per-call timeout. Every status is returned
    /// as a response; only a transport failure or the caller's cancellation throws. No
    /// <c>ConfigureAwait</c>, so the caller's continuation resumes on its synchronization
    /// context (the Unity main thread in the Editor). It never logs a header, a body or a URL.
    /// </summary>
    public sealed class EditorHttpTransport : IHttpTransport, IDisposable
    {
        /// <summary>The default per-call timeout.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Bodies larger than this are refused (the largest PingCore API answer the plugin reads is a few hundred KiB).</summary>
        public const int MaxBodyBytes = 8 * 1024 * 1024;

        private readonly HttpClient client;
        private readonly TimeSpan timeout;

        /// <param name="timeout">Per-call timeout; <see cref="DefaultTimeout"/> when null.</param>
        public EditorHttpTransport(TimeSpan? timeout = null)
        {
            this.timeout = timeout ?? DefaultTimeout;
            client = new HttpClient(CreateHandler(), disposeHandler: true)
            {
                // The per-call timeout below governs; this only stops HttpClient's own 100 s default from cutting in first.
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = MaxBodyBytes,
            };
        }

        /// <inheritdoc />
        public async Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new PingCoreTransportException("The plugin only sends https requests.");
            }

            using (var message = new HttpRequestMessage(new HttpMethod(request.Method), uri))
            using (var timeoutSource = new CancellationTokenSource(timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token))
            {
                foreach (KeyValuePair<string, string> header in request.Headers)
                {
                    if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                if (request.Body != null)
                {
                    message.Content = new StringContent(request.Body, new UTF8Encoding(false), "application/json");
                }

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, linked.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new PingCoreTransportException($"No answer within {timeout.TotalSeconds:0} s.");
                }
                catch (HttpRequestException e)
                {
                    // The message of an HttpRequestException can quote the URL; keep only its type.
                    throw new PingCoreTransportException($"The request failed ({e.GetType().Name}{InnerName(e)}).");
                }

                using (response)
                {
                    string body = response.Content == null ? null : await response.Content.ReadAsStringAsync();
                    return new PingCoreHttpResponse((int)response.StatusCode, Headers(response), string.IsNullOrEmpty(body) ? null : body);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose() => client.Dispose();

        /// <summary>The handler: redirects never followed, no cookies.</summary>
        internal static HttpClientHandler CreateHandler()
        {
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
        }

        private static string InnerName(Exception e) => e.InnerException == null ? string.Empty : ": " + e.InnerException.GetType().Name;

        private static IReadOnlyDictionary<string, string> Headers(HttpResponseMessage response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Add(headers, response.Headers);
            if (response.Content != null)
            {
                Add(headers, response.Content.Headers);
            }

            return headers;
        }

        private static void Add(Dictionary<string, string> into, HttpHeaders headers)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
            {
                into[header.Key] = string.Join(", ", header.Value ?? Enumerable.Empty<string>());
            }
        }
    }
}
