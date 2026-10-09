using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>Getting a token: the shared wait, then the provider, the token store, or an anonymous issue.</summary>
    public sealed partial class PlayerTokenCache
    {
        private static async Task<DiscoveryResult<PlayerToken>> WaitAsync(Task<DiscoveryResult<PlayerToken>> shared, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled || shared.IsCompleted)
            {
                return await shared;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                Task first = await Task.WhenAny(shared, cancelled.Task);
                if (first == shared)
                {
                    return await shared;
                }
            }

            return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Cancelled, "cancelled while waiting for the player token");
        }

        private async void RunFlight(TaskCompletionSource<DiscoveryResult<PlayerToken>> flight)
        {
            DiscoveryResult<PlayerToken> result;
            try
            {
                result = await AcquireAsync();
            }
            catch (Exception e)
            {
                result = DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Unexpected, "player token acquisition failed: " + e.GetType().Name);
            }

            lock (sync)
            {
                inflight = null;
            }

            flight.TrySetResult(result);
        }

        private async Task<DiscoveryResult<PlayerToken>> AcquireAsync()
        {
            Func<CancellationToken, Task<string>> signedProvider;
            lock (sync)
            {
                signedProvider = provider;
            }

            if (signedProvider != null)
            {
                return await FromProviderAsync(signedProvider);
            }

            DiscoveryResult<PlayerToken> loaded = TryLoad();
            if (loaded != null)
            {
                return loaded;
            }

            return await IssueAsync();
        }

        private async Task<DiscoveryResult<PlayerToken>> FromProviderAsync(Func<CancellationToken, Task<string>> signedProvider)
        {
            string jwt;
            try
            {
                jwt = await signedProvider(lifetime);
            }
            catch (OperationCanceledException)
            {
                return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Cancelled, "the signed token provider was cancelled");
            }
            catch (Exception e)
            {
                Write(DiscoveryLogLevel.Error, "the signed token provider failed: " + e.GetType().Name);
                return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Unexpected, "the signed token provider failed: " + e.GetType().Name);
            }

            if (string.IsNullOrEmpty(jwt))
            {
                return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Unauthorized, "the signed token provider returned no token");
            }

            if (!JwtClaims.TryRead(jwt, out string subject, out DateTimeOffset expiresAt))
            {
                return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Unauthorized, "the signed token provider returned a token whose payload does not parse");
            }

            var token = new PlayerToken(PlayerTokenKind.Signed, jwt, subject, expiresAt);
            if (!IsUsable(token, scheduler.UtcNow))
            {
                return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Unauthorized, "the signed token provider returned a token that is expired or about to expire");
            }

            Adopt(token, PlayerTokenChange.SignedSet);
            return DiscoveryResult<PlayerToken>.Success(token, 0, null);
        }

        private DiscoveryResult<PlayerToken> TryLoad()
        {
            StoredPlayerToken stored;
            try
            {
                if (!store.TryLoad(storeKey, out stored) || stored == null)
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                Write(DiscoveryLogLevel.Warning, "the token store could not be read: " + e.GetType().Name);
                return null;
            }

            var token = new PlayerToken(PlayerTokenKind.Anonymous, stored.Token, stored.PlayerId, stored.ExpiresAt);
            if (string.IsNullOrEmpty(stored.Token) || string.IsNullOrEmpty(stored.PlayerId) || !IsUsable(token, scheduler.UtcNow))
            {
                SafeDelete();
                return null;
            }

            lock (sync)
            {
                hadAnonymous = true;
            }

            Adopt(token, PlayerTokenChange.Loaded);
            return DiscoveryResult<PlayerToken>.Success(token, 0, null);
        }

        private async Task<DiscoveryResult<PlayerToken>> IssueAsync()
        {
            TimeSpan? blocked = gate.BlockedFor(scheduler.UtcNow);
            if (blocked.HasValue)
            {
                return new DiscoveryResult<PlayerToken>(DiscoveryOutcome.RateLimited, 0, null, "anonymous player token issuance is rate limited; not sending before Retry-After has passed", null, blocked, null, null);
            }

            TimeSpan wait = gate.Reserve(scheduler.UtcNow);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await scheduler.DelayAsync(wait, lifetime);
                }
                catch (OperationCanceledException)
                {
                    return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Cancelled, "cancelled while pacing player token issuance");
                }
            }

            DiscoveryRequest request = DiscoveryRequest.Create("POST", "/v1/apps/" + DiscoveryCaller.Segment(appPublicId) + "/player-tokens", "POST /v1/apps/{publicId}/player-tokens");
            DiscoveryResult<PlayerTokenResponse> answer = await caller.SendAsync<PlayerTokenResponse>(request, lifetime);
            if (answer.Outcome == DiscoveryOutcome.RateLimited)
            {
                gate.RecordRateLimited(scheduler.UtcNow, answer.RetryAfter);
                Write(DiscoveryLogLevel.Warning, "anonymous player token issuance answered 429");
                return DiscoveryResult<PlayerToken>.From(answer);
            }

            if (!answer.IsOk)
            {
                Write(answer.Outcome == DiscoveryOutcome.Cancelled ? DiscoveryLogLevel.Info : DiscoveryLogLevel.Warning, "anonymous player token issuance failed: " + answer);
                return DiscoveryResult<PlayerToken>.From(answer);
            }

            PlayerTokenResponse body = answer.Value;
            if (string.IsNullOrEmpty(body.Token) || string.IsNullOrEmpty(body.PlayerId))
            {
                return DiscoveryResult<PlayerToken>.Local(DiscoveryOutcome.Unexpected, "the issued player token answer has no token or playerId");
            }

            var token = new PlayerToken(PlayerTokenKind.Anonymous, body.Token, body.PlayerId, DateTimeOffset.FromUnixTimeMilliseconds(body.ExpiresAt));
            bool refreshed;
            lock (sync)
            {
                refreshed = hadAnonymous;
                hadAnonymous = true;
            }

            try
            {
                store.Save(storeKey, new StoredPlayerToken(body.Token, body.PlayerId, body.ExpiresAt));
            }
            catch (Exception e)
            {
                Write(DiscoveryLogLevel.Warning, "the token store could not be written: " + e.GetType().Name);
            }

            Adopt(token, refreshed ? PlayerTokenChange.Refreshed : PlayerTokenChange.Issued);
            return new DiscoveryResult<PlayerToken>(DiscoveryOutcome.Ok, answer.Status, null, null, null, null, answer.RateLimit, token);
        }

        private void SafeDelete()
        {
            try
            {
                store.Delete(storeKey);
            }
            catch (Exception e)
            {
                Write(DiscoveryLogLevel.Warning, "the token store could not be cleared: " + e.GetType().Name);
            }
        }
    }
}
