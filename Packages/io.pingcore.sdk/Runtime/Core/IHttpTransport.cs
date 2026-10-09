using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Core
{
    /// <summary>
    /// The HTTP seam for Core. Unity implements it with <c>UnityWebRequest</c>; the plain
    /// .NET test build implements it with <c>HttpClient</c>. An implementation returns every
    /// HTTP status as a response and throws only for a transport failure or cancellation.
    /// </summary>
    public interface IHttpTransport
    {
        /// <summary>Sends one request and returns its response.</summary>
        Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken);
    }

    /// <summary>One outgoing HTTP request.</summary>
    public sealed class PingCoreHttpRequest
    {
        /// <param name="method">HTTP method in upper case.</param>
        /// <param name="url">Absolute URL.</param>
        /// <param name="headers">Request headers. Never logged, because they can carry a bearer token.</param>
        /// <param name="body">UTF-8 JSON body, or null for none.</param>
        public PingCoreHttpRequest(string method, string url, IReadOnlyDictionary<string, string> headers, string body)
        {
            Method = method;
            Url = url;
            Headers = headers ?? new Dictionary<string, string>();
            Body = body;
        }

        /// <summary>HTTP method in upper case.</summary>
        public string Method { get; }

        /// <summary>Absolute URL.</summary>
        public string Url { get; }

        /// <summary>Request headers.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>UTF-8 JSON body, or null.</summary>
        public string Body { get; }
    }

    /// <summary>One HTTP response, whatever its status.</summary>
    public sealed class PingCoreHttpResponse
    {
        /// <param name="status">HTTP status code.</param>
        /// <param name="headers">Response headers.</param>
        /// <param name="body">Response body as text, or null when empty.</param>
        public PingCoreHttpResponse(int status, IReadOnlyDictionary<string, string> headers, string body)
        {
            Status = status;
            Headers = headers ?? new Dictionary<string, string>();
            Body = body;
        }

        /// <summary>HTTP status code.</summary>
        public int Status { get; }

        /// <summary>Response headers.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>Response body as text, or null.</summary>
        public string Body { get; }
    }
}
