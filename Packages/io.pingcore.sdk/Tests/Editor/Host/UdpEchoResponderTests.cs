using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PingCore.Discovery.Host.Tests.Editor
{
    /// <summary>The DSCV1 echo responder over a real loopback socket, plus its pure frame and cap rules.</summary>
    public sealed class UdpEchoResponderTests
    {
        private static byte[] Challenge(byte seed)
        {
            var frame = new byte[21];
            Array.Copy(new byte[] { 0x44, 0x53, 0x43, 0x56, 0x31 }, frame, 5);
            for (int i = 5; i < 21; i++)
            {
                frame[i] = (byte)(seed + (i * 7));
            }

            return frame;
        }

        private static Prober NewProber() => new Prober();

        [Test]
        public async Task TheChallengeIsEchoedByteForByteFromTheBoundPort()
        {
            using (UdpEchoResponder responder = UdpEchoResponder.Bind(0))
            using (Prober prober = NewProber())
            {
                byte[] challenge = Challenge(1);
                await prober.Client.SendAsync(challenge, challenge.Length, new IPEndPoint(IPAddress.Loopback, responder.Port));

                UdpReceiveResult? reply = await prober.ReceiveWithin(3000);

                Assert.That(reply.HasValue, Is.True, "no echo");
                Assert.That(reply.Value.Buffer, Is.EqualTo(challenge));
                Assert.That(reply.Value.RemoteEndPoint.Port, Is.EqualTo(responder.Port), "the reply comes from the socket the challenge reached");
                Assert.That(responder.Echoed, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task EveryOtherDatagramIsDroppedAndAChallengeAfterThemIsStillAnswered()
        {
            byte[] wrongMagic = Challenge(2);
            wrongMagic[4] = 0x32; // DSCV2
            var others = new List<byte[]>
            {
                new byte[20],
                Challenge(3).AsSpanCopy(20),
                Concat(Challenge(4), new byte[] { 0 }),
                wrongMagic,
                new byte[] { 0x44, 0x53, 0x43, 0x56, 0x31 },
            };
            using (UdpEchoResponder responder = UdpEchoResponder.Bind(0))
            using (Prober prober = NewProber())
            {
                var target = new IPEndPoint(IPAddress.Loopback, responder.Port);
                foreach (byte[] datagram in others)
                {
                    await prober.Client.SendAsync(datagram, datagram.Length, target);
                }

                Assert.That(await prober.ReceiveWithin(300), Is.Null, "no reply to anything but the exact challenge");
                await HostWait.Until(() => responder.Dropped == others.Count, "every wrong datagram counted as dropped");

                byte[] challenge = Challenge(5);
                await prober.Client.SendAsync(challenge, challenge.Length, target);
                UdpReceiveResult? reply = await prober.ReceiveWithin(3000);
                Assert.That(reply.HasValue, Is.True, "control: the same socket still answers a real challenge");
                Assert.That(reply.Value.Buffer, Is.EqualTo(challenge));
                Assert.That(responder.Echoed, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task EachSourceIsCappedPerWindowAndTheCapResetsWhenTheWindowPasses()
        {
            DateTimeOffset now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
            using (UdpEchoResponder responder = UdpEchoResponder.Bind(0, new EchoRateLimiter(3, 100), () => now))
            using (Prober prober = NewProber())
            {
                var target = new IPEndPoint(IPAddress.Loopback, responder.Port);
                for (byte i = 0; i < 5; i++)
                {
                    byte[] challenge = Challenge((byte)(10 + i));
                    await prober.Client.SendAsync(challenge, challenge.Length, target);
                }

                int replies = 0;
                while (await prober.ReceiveWithin(500) != null)
                {
                    replies++;
                }

                Assert.That(replies, Is.EqualTo(3), "three replies within the window");
                Assert.That(responder.Throttled, Is.EqualTo(2));

                now += EchoRateLimiter.Window;
                byte[] later = Challenge(20);
                await prober.Client.SendAsync(later, later.Length, target);
                Assert.That((await prober.ReceiveWithin(3000)).HasValue, Is.True, "a new window answers again");
            }
        }

        [Test]
        public void TheFrameRuleAcceptsExactlyMagicPlusSixteenBytes()
        {
            Assert.That(EchoFrame.IsChallenge(Challenge(1), 21), Is.True);
            Assert.That(EchoFrame.IsChallenge(Challenge(1), 20), Is.False);
            Assert.That(EchoFrame.IsChallenge(Concat(Challenge(1), new byte[] { 9 }), 22), Is.False);
            byte[] lower = Challenge(1);
            lower[0] = 0x64; // 'd'
            Assert.That(EchoFrame.IsChallenge(lower, 21), Is.False);
            Assert.That(EchoFrame.IsChallenge(null, 21), Is.False);
        }

        [Test]
        public void TheCapCountsPerSourceAndAllSourcesAndBoundsTheTable()
        {
            var t0 = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
            var limiter = new EchoRateLimiter(2, 3);
            Assert.That(limiter.TryAcquire("198.51.100.1", t0), Is.True);
            Assert.That(limiter.TryAcquire("198.51.100.1", t0), Is.True);
            Assert.That(limiter.TryAcquire("198.51.100.1", t0), Is.False, "per-source cap");
            Assert.That(limiter.TryAcquire("198.51.100.2", t0), Is.True, "another source has its own budget");
            Assert.That(limiter.TryAcquire("198.51.100.3", t0), Is.False, "global cap of 3");
            Assert.That(limiter.TryAcquire("198.51.100.1", t0 + EchoRateLimiter.Window), Is.True, "the window resets");

            var full = new EchoRateLimiter(EchoRateLimiter.PerSourceLimit, 10000);
            for (int i = 0; i < EchoRateLimiter.MaxSources; i++)
            {
                Assert.That(full.TryAcquire("10.0." + (i / 250) + "." + (i % 250), t0), Is.True);
            }

            Assert.That(full.TryAcquire("10.9.9.9", t0 + TimeSpan.FromSeconds(1)), Is.False, "a full table refuses a new source");
            Assert.That(full.TryAcquire("10.9.9.9", t0 + EchoRateLimiter.Window), Is.True, "expired sources make room");
            Assert.That(full.TrackedSources, Is.LessThanOrEqualTo(EchoRateLimiter.MaxSources));
        }

        [Test]
        public void BindRejectsAPortOutsideTheRangeAndTryBindReportsABusyPort()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => UdpEchoResponder.Bind(65536));
            using (UdpEchoResponder first = UdpEchoResponder.Bind(0))
            {
                Assert.That(UdpEchoResponder.TryBind(first.Port, out UdpEchoResponder second, out string error), Is.False);
                Assert.That(second, Is.Null);
                Assert.That(error, Does.Contain(first.Port.ToString()));
            }
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Array.Copy(a, result, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }

    /// <summary>
    /// A loopback prober that keeps at most one receive outstanding, so a receive that timed out is
    /// reused by the next wait instead of silently taking the next datagram.
    /// </summary>
    internal sealed class Prober : IDisposable
    {
        private Task<UdpReceiveResult> pending;

        public UdpClient Client { get; } = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        /// <summary>The next datagram within <paramref name="ms"/>, or null.</summary>
        public async Task<UdpReceiveResult?> ReceiveWithin(int ms)
        {
            if (pending == null)
            {
                pending = Client.ReceiveAsync();
            }

            Task done = await Task.WhenAny(pending, Task.Delay(ms));
            if (done != pending)
            {
                return null;
            }

            UdpReceiveResult result = await pending;
            pending = null;
            return result;
        }

        public void Dispose() => Client.Dispose();
    }

    internal static class ByteArrayTestExtensions
    {
        public static byte[] AsSpanCopy(this byte[] source, int length)
        {
            var result = new byte[length];
            Array.Copy(source, result, length);
            return result;
        }
    }
}
