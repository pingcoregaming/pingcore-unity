using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// The latency probe's timing on a slow main thread: the real <see cref="ClientWebSocketEchoTransport"/>
    /// against a loopback beacon that holds each pong exactly 7 ms, with every continuation of the
    /// code under test pumped once per 30 fps frame (<see cref="FramePump"/>). A stamp read after an
    /// <c>await</c> resumed on that thread reads a whole frame (a measured run's 67, 100, 167 ms
    /// medians); the stamp read where the socket read completed reads the beacon.
    /// <para>
    /// Both tests assert a 4-10 ms window around the 7 ms hold on a real loopback socket and real thread-pool
    /// threads, so on a heavily loaded machine (a build or a scan saturating the CPU) a median can fall outside it and
    /// a test flake. Rerun on an idle machine, and widen the window only if it fails there.
    /// </para>
    /// </summary>
    public sealed class LatencyTimingTests
    {
        private const double FrameMs = 1000.0 / 30;
        private static readonly TimeSpan BeaconHold = TimeSpan.FromMilliseconds(7);

        /// <summary>The probe's median is the beacon's hold, within the 4-10 ms window (may flake under heavy load, see above).</summary>
        [Test]
        public void TheMedianFollowsTheBeaconNotTheFrameRate()
        {
            using (LoopbackBeacon beacon = LoopbackBeacon.Start(BeaconHold))
            {
                var pump = new FramePump(FrameMs);
                var locations = new List<Location> { new Location { Id = "loopback", Name = "Loopback", PingUrl = beacon.Url, Enabled = true } };
                LatencyResult result = pump.Run(() => new LatencyProbe(new TestScheduler()).MeasureAsync(locations, new LatencyProbeOptions(), CancellationToken.None));

                Assert.That(result.Outcome, Is.EqualTo(PingCore.Core.Discovery.DiscoveryOutcome.Ok), string.Join("; ", result.Detail));
                int median = result.Medians["loopback"];
                TestContext.WriteLine($"probe median {median} ms against an injected {BeaconHold.TotalMilliseconds} ms, main thread pumped every {FrameMs:F1} ms ({pump.Frames} frames)");
                Assert.That(beacon.Pings, Is.EqualTo(6), "one warm-up plus five samples, all through the real socket");
                Assert.That(pump.Frames, Is.GreaterThanOrEqualTo(6), "every sample crossed at least one slow frame");
                Assert.That(median, Is.InRange(4, 10), "within 3 ms of the beacon's 7 ms; a main-thread stamp reads 33 ms or more");
            }
        }

        /// <summary>The control: the main-thread reading is a whole frame, the completing-thread one within 4-10 ms (may flake under heavy load, see above).</summary>
        [Test]
        public void TheControlAStampReadAfterTheMainThreadResumesReadsAWholeFrame()
        {
            using (LoopbackBeacon beacon = LoopbackBeacon.Start(BeaconHold))
            {
                var pump = new FramePump(FrameMs);
                (List<double> stamped, List<double> resumed) = pump.Run(() => BothReadingsAsync(beacon.Url));

                Assert.That(LatencyMath.TryMedian(stamped, 5, out int stampedMedian, out _), Is.True);
                Assert.That(LatencyMath.TryMedian(resumed, 5, out int resumedMedian, out _), Is.True);
                TestContext.WriteLine($"same five pings: completing-thread stamp median {stampedMedian} ms, main-thread stamp median {resumedMedian} ms (frame {FrameMs:F1} ms)");

                // The old probe read its stamp where the main thread resumed: this reading.
                Assert.That(resumedMedian, Is.GreaterThanOrEqualTo(33), "the main-thread reading is quantised to the frame");
                Assert.That(stampedMedian, Is.InRange(4, 10), "the completing-thread reading of the very same pongs is the beacon's");
            }
        }

        /// <summary>The probe's sample sequence (warm-up, then five), reading each pong both ways.</summary>
        private static async Task<(List<double> Stamped, List<double> Resumed)> BothReadingsAsync(string url)
        {
            var stamped = new List<double>();
            var resumed = new List<double>();
            using (IWebSocketEchoSession session = await new ClientWebSocketEchoTransport().ConnectAsync(url, CancellationToken.None))
            {
                for (int i = 0; i < 6; i++)
                {
                    Task<EchoMessage> receive = session.ReceiveTextAsync(CancellationToken.None);
                    long start = Stopwatch.GetTimestamp();
                    await session.SendTextAsync("ping", CancellationToken.None);
                    EchoMessage pong = await receive;
                    long afterResume = Stopwatch.GetTimestamp();
                    Assert.That(pong.Text, Is.EqualTo("pong"));
                    if (i == 0)
                    {
                        continue;
                    }

                    stamped.Add(Ms(pong.ReceivedTimestamp - start));
                    resumed.Add(Ms(afterResume - start));
                }
            }

            return (stamped, resumed);
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
