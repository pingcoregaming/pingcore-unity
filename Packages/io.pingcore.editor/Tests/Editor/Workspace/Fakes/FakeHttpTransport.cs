using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Tests.Fakes
{
    /// <summary>
    /// A scripted <see cref="IHttpTransport"/> for <c>PingCoreApiClient</c> tests: every request is
    /// recorded, answers are dequeued in order (or <see cref="Handler"/> computes one), and
    /// <see cref="ThrowNext"/> makes the next send throw, as a real transport does when no answer
    /// arrives. Nothing touches the network.
    /// </summary>
    public sealed class FakeHttpTransport : IHttpTransport
    {
        private readonly Queue<PingCoreHttpResponse> answers = new Queue<PingCoreHttpResponse>();

        public List<PingCoreHttpRequest> Requests { get; } = new List<PingCoreHttpRequest>();

        /// <summary>Computes an answer when the queue is empty.</summary>
        public Func<PingCoreHttpRequest, PingCoreHttpResponse> Handler { get; set; }

        /// <summary>The next send throws this.</summary>
        public Exception ThrowNext { get; set; }

        public FakeHttpTransport Answer(int status, string body, IReadOnlyDictionary<string, string> headers = null)
        {
            answers.Enqueue(new PingCoreHttpResponse(status, headers, body));
            return this;
        }

        public Task<PingCoreHttpResponse> SendAsync(PingCoreHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowNext != null)
            {
                Exception e = ThrowNext;
                ThrowNext = null;
                throw e;
            }

            if (answers.Count > 0)
            {
                return Task.FromResult(answers.Dequeue());
            }

            if (Handler != null)
            {
                return Task.FromResult(Handler(request));
            }

            throw new InvalidOperationException("FakeHttpTransport: no answer scripted for " + request.Method);
        }
    }
}
