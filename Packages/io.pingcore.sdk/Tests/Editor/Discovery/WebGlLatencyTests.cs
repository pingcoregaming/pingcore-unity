using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// The WebGL beacon transport can only run in a WebGL player, which EditMode cannot run, so these pin the parts that do
    /// not need a browser: the clock glue that turns the plug-in's message age into the arrival timestamp (and so the
    /// samples the median is taken from), the platform default, and the contract between <c>WebGlEchoTransport.cs</c> and
    /// <c>PingCoreLatency.jslib</c> (every imported function exists, the C# only compiles for WebGL players, the plug-in is
    /// WebGL-only, reads <c>performance.now()</c> first in <c>onmessage</c> and measures the age from the event's
    /// <c>timeStamp</c>).
    /// </summary>
    public sealed class WebGlLatencyTests
    {
        private const long Frequency = 10_000_000; // Stopwatch ticks per second on Windows: 100 ns

        [Test]
        public void TheArrivalIsTheCSharpClockMinusTheAgeTheBrowserMeasured()
        {
            Assert.That(WebGlEchoClock.ArrivalTimestamp(1_400_000, 32_700, Frequency), Is.EqualTo(1_073_000), "32.7 ms is 327,000 ticks");
            Assert.That(WebGlEchoClock.ArrivalTimestamp(1_400_000, 0, Frequency), Is.EqualTo(1_400_000));
            Assert.That(WebGlEchoClock.ArrivalTimestamp(1_400_000, -5, Frequency), Is.EqualTo(1_400_000), "a negative age counts as 0");
            Assert.That(WebGlEchoClock.ArrivalTimestamp(1_000, 5_000, Frequency), Is.EqualTo(0), "never below 0");
            Assert.That(WebGlEchoClock.ArrivalTimestamp(10, 1_500, 1_000), Is.EqualTo(8), "1.5 ticks of a 1 kHz clock round half away from zero to 2");
            Assert.That(WebGlEchoClock.ArrivalTimestamp(long.MaxValue, int.MaxValue, Frequency), Is.EqualTo(long.MaxValue - 21_474_836_470L), "no overflow at the largest age");
            Assert.Throws<ArgumentOutOfRangeException>(() => WebGlEchoClock.ArrivalTimestamp(1, 1, 0));
        }

        [Test]
        public void AnEventDispatchedAfterAUnityFrameIsTakenBackToWhenTheBrowserCreatedIt()
        {
            // Five pings of a 7.3 ms beacon in a 30 fps player. The browser creates each pong's message event 7.3 ms after
            // the send (event.timeStamp) but dispatches it only once the Unity frame running then has ended, 0 to 30.2 ms
            // later, and the copy into the WASM heap takes another 0.05 ms. The plug-in's age (performance.now() at the
            // call minus event.timeStamp) puts every arrival back at 7.3 ms, so the median is 7, not 7 plus a frame.
            double[] queuedMs = { 0.0, 4.1, 12.6, 21.9, 30.2 };
            var samples = new List<double>();
            var seenByCSharpOnly = new List<double>();
            for (int i = 0; i < queuedMs.Length; i++)
            {
                double sendMs = 5_000 + (i * 100);
                double eventTimeStampMs = sendMs + 7.3;
                double callMs = eventTimeStampMs + queuedMs[i] + 0.05;
                int ageMicros = PlugInAgeMicros(eventTimeStampMs, callMs, callMs);
                long arrival = WebGlEchoClock.ArrivalTimestamp(Ticks(callMs), ageMicros, Frequency);
                samples.Add((arrival - Ticks(sendMs)) * 1000.0 / Frequency);
                seenByCSharpOnly.Add((Ticks(callMs) - Ticks(sendMs)) * 1000.0 / Frequency);
            }

            Assert.That(samples, Is.All.EqualTo(7.3).Within(1e-3));
            Assert.That(LatencyMath.TryMedian(samples, 5, out int median, out bool clamped), Is.True);
            Assert.That((median, clamped), Is.EqualTo((7, false)));

            // Without the age, C#'s own clock would report the frame: a median of 19.95 ms, 20.
            Assert.That(LatencyMath.TryMedian(seenByCSharpOnly, 5, out int frameBound, out _), Is.True);
            Assert.That(frameBound, Is.EqualTo(20));

            // A loopback-fast beacon whose pongs wait 20 ms behind a frame: 0.33 ms round trips are reported as 1, clamped,
            // never 0.
            double fastCallMs = 1_000 + 0.33 + 20;
            int fastAge = PlugInAgeMicros(1_000 + 0.33, fastCallMs, fastCallMs);
            double fast = (WebGlEchoClock.ArrivalTimestamp(Ticks(fastCallMs), fastAge, Frequency) - Ticks(1_000)) * 1000.0 / Frequency;
            Assert.That(fast, Is.EqualTo(0.33).Within(1e-3));
            Assert.That(LatencyMath.TryMedian(Enumerable.Repeat(fast, 5).ToList(), 5, out int one, out bool wasClamped), Is.True);
            Assert.That((one, wasClamped), Is.EqualTo((1, true)));
        }

        [Test]
        public void AnImplausibleEventTimeStampFallsBackToTheHandlersOwnReading()
        {
            // The handler read performance.now() at 2,000 ms and calls C# at 2,000.05 ms.
            Assert.That(PlugInAgeMicros(0, 2_000, 2_000.05), Is.EqualTo(50), "no timeStamp");
            Assert.That(PlugInAgeMicros(1.7e12, 2_000, 2_000.05), Is.EqualTo(50), "an epoch-based timeStamp (older browsers)");
            Assert.That(PlugInAgeMicros(2_000.5, 2_000, 2_000.05), Is.EqualTo(50), "a timeStamp after the handler's own reading");
            Assert.That(PlugInAgeMicros(1_990, 2_000, 2_000.05), Is.EqualTo(10_050), "a plausible one: 10 ms in the queue");
        }

        [Test]
        public void OutsideAWebGlPlayerTheProbeIsSupportedAndUsesClientWebSocket()
        {
            Assert.That(LatencyProbe.IsSupported, Is.True);
            Assert.That(LatencyProbe.CreateDefaultTransport(), Is.InstanceOf<ClientWebSocketEchoTransport>());
        }

        [Test]
        public void ThePlugInDefinesEveryFunctionTheTransportImportsAndOnlyWebGlPlayersCompileEither()
        {
            string folder = Path.Combine(PackageRoot(), "Runtime", "Discovery", "Latency", "WebGL");
            string cs = File.ReadAllText(Path.Combine(folder, "WebGlEchoTransport.cs")).Replace("\r\n", "\n");
            string js = File.ReadAllText(Path.Combine(folder, "PingCoreLatency.jslib")).Replace("\r\n", "\n");
            string meta = File.ReadAllText(Path.Combine(folder, "PingCoreLatency.jslib.meta")).Replace("\r\n", "\n");

            Assert.That(cs.StartsWith("#if UNITY_WEBGL && !UNITY_EDITOR\n", StringComparison.Ordinal) && cs.TrimEnd().EndsWith("#endif", StringComparison.Ordinal),
                "the whole transport is inside the WebGL-player define, so every other target compiles without the plug-in");

            List<string> imports = Regex.Matches(cs, @"\[DllImport\(""__Internal""\)\]\s*private static extern \w+ (\w+)\(").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.That(imports, Is.EquivalentTo(new[] { "PingCoreLatency_Init", "PingCoreLatency_Connect", "PingCoreLatency_Send", "PingCoreLatency_Close" }));
            foreach (string name in imports)
            {
                Assert.That(Regex.IsMatch(js, @"^\s{4}" + name + @": function \(", RegexOptions.Multiline), Is.True, "the plug-in has no " + name);
            }

            Assert.That(Regex.IsMatch(js, @"onmessage = function \(event\) \{\n(\s*//[^\n]*\n)*\s*var stamp = performance\.now\(\);"), Is.True,
                "performance.now() is the first statement of onmessage");
            Assert.That(js, Does.Contain("var created = event.timeStamp;\n            if (!(created > 0 && created <= stamp)) {\n                created = stamp;\n            }"),
                "the age is measured from the event's timeStamp, with the fallback PlugInAgeMicros models");
            Assert.That(js, Does.Contain("var ageMicros = Math.min(2147483647, Math.max(0, Math.round((performance.now() - created) * 1000)));"),
                "the age runs to the moment just before the call into C#, in whole microseconds that fit an int");
            Assert.That(js, Does.Contain("makeDynCall('viiii', 'PingCoreLatency.onMessage')"), "onMessage(socket, text, bytes, ageMicros) is four ints");
            Assert.That(cs, Does.Contain("private delegate void MessageCallback(int socket, IntPtr text, int bytes, int ageMicros);"));

            Assert.That(meta, Does.Contain("PluginImporter:"));
            Assert.That(Regex.IsMatch(meta, @"Any: \n    second:\n      enabled: 0"), Is.True, "not for every platform");
            Assert.That(Regex.IsMatch(meta, @"Editor: Editor\n    second:\n      enabled: 0"), Is.True, "not in the editor");
            Assert.That(Regex.IsMatch(meta, @"WebGL: WebGL\n    second:\n      enabled: 1"), Is.True, "WebGL players only");
        }

        /// <summary>
        /// The plug-in's age, as <c>onmessage</c> in <c>PingCoreLatency.jslib</c> computes it (the contract test above pins
        /// those lines): <paramref name="handlerNowMs"/> is its first <c>performance.now()</c>, <paramref name="callMs"/> the
        /// one just before the call into C#.
        /// </summary>
        private static int PlugInAgeMicros(double eventTimeStampMs, double handlerNowMs, double callMs)
        {
            double created = eventTimeStampMs > 0 && eventTimeStampMs <= handlerNowMs ? eventTimeStampMs : handlerNowMs;
            double micros = Math.Max(0, Math.Floor(((callMs - created) * 1000) + 0.5));
            return (int)Math.Min(int.MaxValue, micros);
        }

        /// <summary>Milliseconds on the page's clock as <see cref="Frequency"/> ticks: the probe's Stopwatch runs on the same clock.</summary>
        private static long Ticks(double ms)
        {
            return (long)Math.Round(ms * Frequency / 1000.0, MidpointRounding.AwayFromZero);
        }

        private static string PackageRoot()
        {
            UnityEditor.PackageManager.PackageInfo info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(LatencyProbe).Assembly);
            Assert.That(info, Is.Not.Null, "io.pingcore.sdk is not resolved as a package");
            return info.resolvedPath;
        }
    }
}
