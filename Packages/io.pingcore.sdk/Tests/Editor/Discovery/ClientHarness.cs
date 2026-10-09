using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using PingCore.Core.Discovery;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// A <see cref="DiscoveryClient"/> wired to a <see cref="FakeDiscoveryTransport"/>, a manual
    /// <see cref="TestScheduler"/>, a <see cref="MemoryTokenStore"/> and its own issuance gate (so no
    /// test shares the process-wide pacing with another). Collects token events and log lines.
    /// </summary>
    internal sealed class ClientHarness : IDisposable
    {
        public const string AppId = "dscp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        public const string IssuePath = "/v1/apps/dscp_a+/player-tokens";
        public const string TicketsPath = "/v1/apps/dscp_a+/tickets";
        public const string TicketPath = "/v1/apps/dscp_a+/tickets/[^/]+";
        public const string ReservePath = "/v1/apps/dscp_a+/servers/[^/]+/reservations";
        public const string QuickJoinPath = "/v1/apps/dscp_a+/quick-join";
        public const string ReservationPath = "/v1/apps/dscp_a+/reservations/[^/]+";
        public const string ServersPath = "/v1/apps/dscp_a+/servers";
        public const string LocationsPath = "/v1/locations";

        private int issued;

        public ClientHarness(string profile = "", MemoryTokenStore store = null, IssuanceGate gate = null, int maxAttempts = 3, TestScheduler scheduler = null)
        {
            Scheduler = scheduler ?? new TestScheduler();
            Http = new FakeDiscoveryTransport(Scheduler);
            Store = store ?? new MemoryTokenStore();
            Gate = gate ?? new IssuanceGate();
            Client = DiscoveryClient.Create(
                new DiscoveryClientOptions
                {
                    BaseUrl = FakeDiscoveryTransport.BaseUrl,
                    AppPublicId = AppId,
                    Transport = Http,
                    Scheduler = Scheduler,
                    TokenStore = Store,
                    Profile = profile,
                    Log = entry => { lock (Logs) { Logs.Add(entry); } },
                    MaxAttempts = maxAttempts,
                },
                Gate);
            Client.Tokens.Changed += e => { lock (TokenEvents) { TokenEvents.Add(e); } };
        }

        public TestScheduler Scheduler { get; }

        public FakeDiscoveryTransport Http { get; }

        public MemoryTokenStore Store { get; }

        public IssuanceGate Gate { get; }

        public DiscoveryClient Client { get; }

        public List<PlayerTokenEvent> TokenEvents { get; } = new List<PlayerTokenEvent>();

        public List<DiscoveryLogEntry> Logs { get; } = new List<DiscoveryLogEntry>();

        public List<PlayerTokenChange> Changes
        {
            get
            {
                lock (TokenEvents)
                {
                    return TokenEvents.Select(e => e.Change).ToList();
                }
            }
        }

        /// <summary>The anonymous token value the n-th issue (1-based) answers with: distinctive, so a leak scan can find it.</summary>
        public static string AnonToken(int n) => "anon-jwt-" + n + "-s3cr3tZq";

        /// <summary>
        /// Every log line, every token event and the token in hand, as text: what a game could print. Never
        /// contains a token value (<see cref="Leaks"/>).
        /// </summary>
        public List<string> PrintedText()
        {
            var lines = new List<string>();
            lock (Logs)
            {
                lines.AddRange(Logs.Select(l => l.ToString()));
            }

            lock (TokenEvents)
            {
                lines.AddRange(TokenEvents.Select(e => e.ToString()));
            }

            lines.Add(Client.Tokens.Current?.ToString() ?? string.Empty);
            return lines;
        }

        /// <summary>
        /// The lines of <paramref name="lines"/> that carry any of <paramref name="tokens"/>, whole or one of its
        /// dot-separated segments of 8 or more characters (a JWT's payload or signature alone is a leak too). Pure.
        /// </summary>
        public static List<string> Leaks(IEnumerable<string> lines, params string[] tokens)
        {
            var needles = new List<string>();
            foreach (string token in tokens)
            {
                needles.Add(token);
                needles.AddRange(token.Split('.').Where(part => part.Length >= 8 && !part.StartsWith("eyJhbGci", StringComparison.Ordinal)));
            }

            return lines.Where(line => line != null && needles.Any(n => line.IndexOf(n, StringComparison.Ordinal) >= 0)).ToList();
        }

        /// <summary>Asserts the client logged something and that nothing it printed carries a token.</summary>
        public void AssertPrintedNoToken(params string[] tokens)
        {
            lock (Logs)
            {
                NUnit.Framework.Assert.That(Logs, NUnit.Framework.Is.Not.Empty, "the scan needs log lines to scan");
            }

            List<string> leaks = Leaks(PrintedText(), tokens);
            NUnit.Framework.Assert.That(leaks, NUnit.Framework.Is.Empty, "a token reached a log line or an event:\n" + string.Join("\n", leaks));
        }

        /// <summary>The player id the n-th issue answers with.</summary>
        public static string AnonPlayer(int n) => "anon:00000000-0000-4000-8000-" + n.ToString("D12");

        /// <summary>A 200 issue answer valid for 6 h from now on the virtual clock.</summary>
        public Step IssueOk()
        {
            issued++;
            long expiresAt = Scheduler.UtcNow.AddHours(6).ToUnixTimeMilliseconds();
            return Step.Json(200, new JObject
            {
                ["error"] = false,
                ["token"] = AnonToken(issued),
                ["tokenType"] = "anonymous",
                ["playerId"] = AnonPlayer(issued),
                ["expiresAt"] = expiresAt,
                ["expiresIn"] = 21600,
            });
        }

        /// <summary>A reservation 200 body.</summary>
        public Step ReservationOk(string reservationId, bool replayed = false, string playerId = "alice")
        {
            long now = Scheduler.UtcNow.ToUnixTimeMilliseconds();
            return Step.Json(200, new JObject
            {
                ["error"] = false,
                ["reservationId"] = reservationId,
                ["serverId"] = "agent-a-1",
                ["status"] = "pending",
                ["seats"] = 1,
                ["ip"] = "203.0.113.10",
                ["port"] = 27015,
                ["createdAt"] = now,
                ["expiresAt"] = now + 60000,
                ["ownerKind"] = "player",
                ["ownerPlayerId"] = playerId,
                ["expiresIn"] = 60,
                ["replayed"] = replayed,
            });
        }

        /// <summary>A ticket submit 200 body; <paramref name="ttlSeconds"/> from now.</summary>
        public Step SubmitOk(string status = "queued", int ttlSeconds = 300)
        {
            long now = Scheduler.UtcNow.ToUnixTimeMilliseconds();
            return Step.Json(200, new JObject
            {
                ["error"] = false,
                ["ticketId"] = "echoed",
                ["status"] = status,
                ["queue"] = "rush-p2",
                ["expiresIn"] = ttlSeconds,
                ["ownerKind"] = "player",
                ["ownerPlayerId"] = AnonPlayer(1),
                ["joinInProgress"] = false,
                ["minSessionSize"] = null,
                ["relaxAfterSeconds"] = null,
                ["createdAt"] = now,
                ["expiresAt"] = now + (ttlSeconds * 1000L),
            });
        }

        /// <summary>A ticket poll 200 body, queued or matched.</summary>
        public Step PollOk(bool matched, bool backfill = false, long? expiresAt = null)
        {
            long now = Scheduler.UtcNow.ToUnixTimeMilliseconds();
            return Step.Json(200, new JObject
            {
                ["error"] = false,
                ["ticketId"] = "echoed",
                ["status"] = matched ? "matched" : "queued",
                ["queue"] = "rush-p2",
                ["allocationId"] = matched ? "alloc-1" : null,
                ["serverId"] = matched ? "agent-a-1" : null,
                ["ip"] = matched ? "203.0.113.10" : null,
                ["port"] = matched ? (JToken)27015 : null,
                ["backfill"] = backfill,
                ["location"] = matched ? "eu-west-ams" : null,
                ["matchedAt"] = matched ? (JToken)now : null,
                ["ownerKind"] = "player",
                ["ownerPlayerId"] = AnonPlayer(1),
                ["createdAt"] = now,
                ["expiresAt"] = expiresAt ?? now + 300000,
            });
        }

        public void Dispose()
        {
            Client.Dispose();
        }
    }

    /// <summary>Compact JWTs for the signed-token tests: a real base64url header and payload, a dummy signature (the client never verifies).</summary>
    internal static class TestJwt
    {
        public static string Make(string subject, DateTimeOffset expiresAt, string signature = "c2ln")
        {
            string header = Encode("{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"k1\"}");
            string payload = Encode("{\"sub\":\"" + subject + "\",\"aud\":\"" + ClientHarness.AppId + "\",\"exp\":" + expiresAt.ToUnixTimeSeconds() + "}");
            return header + "." + payload + "." + signature;
        }

        private static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
