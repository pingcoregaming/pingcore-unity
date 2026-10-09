using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core.Discovery;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// A ticket poll answered 429 with no readable <c>Retry-After</c> (WebGL hides response headers that
    /// Discovery does not expose to the page): it backs off through <see cref="RetryGovernor"/> and counts as
    /// a failure, so five in a row end the ticket instead of polling every 2 s for ever.
    /// </summary>
    public sealed class TicketRateLimitTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task A429WithoutRetryAfterBacksOffAndFiveInARowEndTheTicketFailed()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                Step limited = Step.Error(429, "Too many requests.");
                h.Http.On("GET", ClientHarness.TicketPath, limited, limited, limited, limited, limited, h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(new TicketOptions { SessionSize = 4 }, None))).Value;
                DateTimeOffset submitted = h.Scheduler.UtcNow;

                await h.Scheduler.RunAsync(ticket.WaitAsync(None));

                var polls = h.Http.To("GET", ClientHarness.TicketPath);
                // Mutation: the old code waited max(0, 2 s) and never counted the 429, so the sixth poll matched at 12 s.
                Assert.That(polls.Count, Is.EqualTo(5));
                Assert.That(polls.Select(p => p.At - submitted), Is.EqualTo(new[] { 2, 4, 8, 16, 26 }.Select(s => TimeSpan.FromSeconds(s))), "2 s, then backoff 2, 4, 8, 10");
                Assert.That(ticket.State, Is.EqualTo(TicketState.Failed));
                Assert.That(ticket.LastError.Outcome, Is.EqualTo(DiscoveryOutcome.RateLimited));
                Assert.That(ticket.LastError.RetryAfter, Is.Null);
            }
        }

        [Test]
        public async Task A429WithRetryAfterIsNotCountedAsAFailure()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                Step limited = Step.Error(429, "Too many requests.", null, ("Retry-After", "1"));
                h.Http.On("GET", ClientHarness.TicketPath, limited, limited, limited, limited, limited, limited, h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(new TicketOptions { SessionSize = 4 }, None))).Value;

                await h.Scheduler.RunAsync(ticket.WaitAsync(None));

                // Control for the test above: six readable 429s in a row still reach the match.
                Assert.That(h.Http.Count("GET", ClientHarness.TicketPath), Is.EqualTo(7));
                Assert.That(ticket.State, Is.EqualTo(TicketState.Matched));
            }
        }
    }
}
