using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// Where a studio-signed token may go: into the <c>Authorization</c> header only. Never into the token
    /// store (which holds the anonymous token alone), never into a log line or an event.
    /// </summary>
    public sealed class TokenSecrecyTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task SetSignedTokenKeepsTheStoredAnonymousTokenAsItWas()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                Assert.That((await h.Client.Tokens.GetAsync(None)).IsOk, Is.True);
                string signed = TestJwt.Make("studio:carol", h.Scheduler.UtcNow.AddMinutes(30), "c2lnLXNlY3JldC1j");
                h.Client.Tokens.SetSignedToken(signed);

                Assert.That(h.Client.Tokens.Current.Kind, Is.EqualTo(PlayerTokenKind.Signed));
                // Mutation: make SetSignedToken save to the store and the stored value is the JWT.
                Assert.That(h.Store.Count, Is.EqualTo(1));
                Assert.That(h.Store.TryLoad(h.Client.Tokens.StoreKey, out StoredPlayerToken stored), Is.True);
                Assert.That(stored.Token, Is.EqualTo(ClientHarness.AnonToken(1)), "still the anonymous token");
                Assert.That(stored.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
                h.AssertPrintedNoToken(signed, ClientHarness.AnonToken(1));
            }
        }

        [Test]
        public void SetSignedTokenOnAnEmptyStoreStoresNothing()
        {
            using (var h = new ClientHarness())
            {
                h.Client.Tokens.SetSignedToken(TestJwt.Make("studio:dave", h.Scheduler.UtcNow.AddMinutes(30)));
                // Mutation: make SetSignedToken save to the store and this is 1.
                Assert.That(h.Store.Count, Is.EqualTo(0));
                Assert.That(h.Store.TryLoad(h.Client.Tokens.StoreKey, out _), Is.False);
            }
        }

        [Test]
        public async Task AProviderTokenAfterA401LeavesTheStoredAnonymousTokenAndStoresNoJwt()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                Assert.That((await h.Client.Tokens.GetAsync(None)).IsOk, Is.True);
                string first = TestJwt.Make("studio:erin", h.Scheduler.UtcNow.AddMinutes(30), "c2lnLXNlY3JldC0x");
                string second = TestJwt.Make("studio:erin", h.Scheduler.UtcNow.AddMinutes(30), "c2lnLXNlY3JldC0y");
                int provided = 0;
                h.Client.Tokens.SetSignedTokenProvider(_ => Task.FromResult(++provided == 1 ? first : second));
                h.Http.On("POST", ClientHarness.ReservePath,
                    Step.Error(401, "The token signature does not verify.", "bad_signature"), h.ReservationOk("r-1"));

                DiscoveryResult<Wire.ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", new ReserveOptions { ReservationId = "r-1" }, None));

                Assert.That(r.IsOk, Is.True, r.ToString());
                Assert.That(provided, Is.EqualTo(2), "the provider was asked again after the 401");
                Assert.That(h.Http.To("POST", ClientHarness.ReservePath)[1].Authorization, Is.EqualTo("Bearer " + second));
                Assert.That(h.Store.Count, Is.EqualTo(1));
                Assert.That(h.Store.TryLoad(h.Client.Tokens.StoreKey, out StoredPlayerToken stored), Is.True);
                Assert.That(stored.Token, Is.EqualTo(ClientHarness.AnonToken(1)), "the stored anonymous token is untouched by the signed path");
                h.AssertPrintedNoToken(first, second, ClientHarness.AnonToken(1));
            }
        }

        [Test]
        public void TheLeakScanFlagsAWholeTokenAndAnyLongSegmentOfIt()
        {
            // The scan behind AssertPrintedNoToken must be able to fail, or the tests above pass vacuously.
            string jwt = TestJwt.Make("studio:x", System.DateTimeOffset.UnixEpoch.AddYears(60), "c2lnLXNlY3JldC0x");
            string payload = jwt.Split('.')[1];
            Assert.That(ClientHarness.Leaks(new[] { "Warning tickets: bearer " + jwt }, jwt), Has.Count.EqualTo(1), "the whole JWT");
            Assert.That(ClientHarness.Leaks(new[] { "sig=c2lnLXNlY3JldC0x" }, jwt), Has.Count.EqualTo(1), "its signature alone");
            Assert.That(ClientHarness.Leaks(new[] { "claims " + payload }, jwt), Has.Count.EqualTo(1), "its payload alone");
            Assert.That(ClientHarness.Leaks(new[] { "Rejected Anonymous playerId=anon:1 token=" + ClientHarness.AnonToken(2) }, ClientHarness.AnonToken(2)), Has.Count.EqualTo(1));
            Assert.That(ClientHarness.Leaks(new[] { "Rejected Signed playerId=studio:x reason=bad_signature" }, jwt, ClientHarness.AnonToken(1)), Is.Empty, "a clean line");
        }
    }
}
