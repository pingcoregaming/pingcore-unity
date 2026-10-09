using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// <c>PingCore.Core</c> must compile under plain netstandard2.1 with no engine reference and
    /// must never block or leave the main thread: waits go through <c>IScheduler</c>. It also does no
    /// runtime reflection (IL2CPP stripping). This scans <c>Runtime/Core/**/*.cs</c>, comments
    /// included, for the banned constructs, and pins the Core asmdef.
    /// </summary>
    public sealed class CoreAsyncBanTests
    {
        private static readonly (string Name, Regex Pattern)[] Bans =
        {
            ("Task.Run", new Regex(@"\bTask\s*\.\s*Run\b")),
            (".Wait(", new Regex(@"\.\s*Wait\s*\(")),
            ("Task.WaitAll", new Regex(@"\bTask\s*\.\s*WaitAll\b")),
            ("Task.WaitAny", new Regex(@"\bTask\s*\.\s*WaitAny\b")),
            (".WaitOne(", new Regex(@"\.\s*WaitOne\s*\(")),
            (".Result", new Regex(@"\.\s*Result\b")),
            (".GetResult()", new Regex(@"\.\s*GetResult\s*\(")),
            ("ConfigureAwait", new Regex(@"\bConfigureAwait\b")),
            ("Task.Delay", new Regex(@"\bTask\s*\.\s*Delay\b")),
            ("Thread.Sleep", new Regex(@"\bThread\s*\.\s*Sleep\b")),
            ("Thread.SpinWait", new Regex(@"\bThread\s*\.\s*SpinWait\b")),
            ("SpinWait.SpinUntil", new Regex(@"\bSpinWait\s*\.\s*SpinUntil\b")),
            ("new Thread", new Regex(@"\bnew\s+(global::)?(System\s*\.\s*Threading\s*\.\s*)?Thread\s*\(")),
            ("System.Threading.Timer", new Regex(@"\bThreading\s*\.\s*Timer\b")),
            ("new Timer", new Regex(@"\bnew\s+Timer\s*\(")),
            ("System.Reflection", new Regex(@"\bSystem\s*\.\s*Reflection\b")),
            // Reflection reached without a System.Reflection using: extension methods, Type members and Enum/Activator.
            ("GetCustomAttribute", new Regex(@"\bGetCustomAttribute\b")),
            ("GetCustomAttributes", new Regex(@"\bGetCustomAttributes\b")),
            ("Enum.GetValues", new Regex(@"\bEnum\s*\.\s*GetValues\b")),
            ("Enum.GetNames", new Regex(@"\bEnum\s*\.\s*GetNames\b")),
            (".GetField(", new Regex(@"\.\s*GetField\s*\(")),
            (".GetProperty(", new Regex(@"\.\s*GetProperty\s*\(")),
            (".GetMethod(", new Regex(@"\.\s*GetMethod\s*\(")),
            ("Activator.CreateInstance", new Regex(@"\bActivator\s*\.\s*CreateInstance\b")),
            ("UnityEngine", new Regex(@"\bUnityEngine\b")),
            ("UnityEditor", new Regex(@"\bUnityEditor\b")),
        };

        private static string CoreRoot => Path.Combine(RepoPaths.PackageRoot, "Runtime", "Core");

        [Test]
        public void CoreSourceUsesNoBannedAsyncOrEngineConstruct()
        {
            List<string> files = Directory.GetFiles(CoreRoot, "*.cs", SearchOption.AllDirectories).ToList();
            Assert.That(files.Count, Is.GreaterThan(5), "Core source not found; the scan root is wrong");

            var hits = new List<string>();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (string ban in Scan(lines[i]))
                    {
                        hits.Add($"{RepoPaths.Relative(RepoPaths.PackageRoot, file)}:{i + 1}: {ban}");
                    }
                }
            }

            Assert.That(hits, Is.Empty, "Banned in PingCore.Core:\n" + string.Join("\n", hits));
        }

        [Test]
        public void TheScannerFlagsEachBannedConstruct()
        {
            var samples = new Dictionary<string, string>
            {
                ["Task.Run"] = "await Task.Run(() => Work());",
                [".Wait("] = "task.Wait(); other.Wait(500); third.Wait(token);",
                ["Task.WaitAll"] = "Task.WaitAll(a, b);",
                ["Task.WaitAny"] = "int i = Task.WaitAny(a, b);",
                [".WaitOne("] = "handle.WaitOne(100);",
                [".Result"] = "var x = task.Result;",
                [".GetResult()"] = "task.GetAwaiter().GetResult();",
                ["ConfigureAwait"] = "await task.ConfigureAwait(false);",
                ["Task.Delay"] = "await Task.Delay(100);",
                ["Thread.Sleep"] = "System.Threading.Thread.Sleep(TimeSpan.FromSeconds(1));",
                ["Thread.SpinWait"] = "Thread.SpinWait(20);",
                ["SpinWait.SpinUntil"] = "SpinWait.SpinUntil(() => done);",
                ["new Thread"] = "var t = new System.Threading.Thread(Loop);",
                ["System.Threading.Timer"] = "private System.Threading.Timer timer;",
                ["new Timer"] = "timer = new Timer(Tick, null, 0, 1000);",
                ["System.Reflection"] = "using System.Reflection;",
                ["GetCustomAttribute"] = "var a = member.GetCustomAttribute<EnumMemberAttribute>();",
                ["GetCustomAttributes"] = "object[] all = type.GetCustomAttributes(false);",
                ["Enum.GetValues"] = "foreach (var v in Enum.GetValues(typeof(DiscoveryReason))) { }",
                ["Enum.GetNames"] = "string[] names = System.Enum.GetNames(typeof(DiscoveryReason));",
                [".GetField("] = "var f = typeof(DiscoveryReason).GetField(name);",
                [".GetProperty("] = "var p = dto.GetType().GetProperty(\"Id\");",
                [".GetMethod("] = "var m = typeof(Session) . GetMethod (\"Join\");",
                ["Activator.CreateInstance"] = "var x = Activator.CreateInstance(type);",
                ["UnityEngine"] = "using UnityEngine;",
                ["UnityEditor"] = "UnityEditor.AssetDatabase.Refresh();",
            };
            Assert.That(samples.Keys, Is.EquivalentTo(Bans.Select(b => b.Name)), "one sample per ban");
            foreach (KeyValuePair<string, string> sample in samples)
            {
                Assert.That(Scan(sample.Value), Does.Contain(sample.Key), sample.Value);
            }

            Assert.That(Scan("new Thread(Loop)"), Does.Contain("new Thread"), "the short form is caught too");
            Assert.That(Scan("var results = response.Results; int resultCode = 0; WaitHandle h; await scheduler.WaitAsync(t); Timeout = x; ThreadId = 1;"), Is.Empty, "near misses are not flagged");
        }

        [Test]
        public void EachReflectionNeedleHasANearMissThatStaysClean()
        {
            var nearMisses = new Dictionary<string, string>
            {
                ["GetCustomAttribute"] = "var data = GetCustomAttributeData(); string GetCustomAttributeName = \"x\";",
                ["GetCustomAttributes"] = "var list = GetCustomAttributesCached; MyGetCustomAttributes(x);",
                ["Enum.GetValues"] = "var v = ReasonEnum.GetValues(); var w = Enum.GetValuesTable;",
                ["Enum.GetNames"] = "var n = ReasonEnum.GetNames(); var w = Enum.GetNamesFrom;",
                [".GetField("] = "var all = row.GetFields(); var f = GetField(name); var g = row.GetFieldName(0);",
                [".GetProperty("] = "var all = row.GetProperties(); var p = GetProperty(name); var q = row.GetPropertyName(0);",
                [".GetMethod("] = "var all = row.GetMethods(); var m = GetMethod(name); var n = row.GetMethodName(0);",
                ["Activator.CreateInstance"] = "var x = MyActivator.CreateInstance(type); var y = Activator.CreateInstanceFrom; var z = factory.CreateInstance();",
            };
            foreach (KeyValuePair<string, string> nearMiss in nearMisses)
            {
                Assert.That(Bans.Any(b => b.Name == nearMiss.Key), Is.True, "no ban named " + nearMiss.Key);
                Assert.That(Scan(nearMiss.Value), Is.Empty, nearMiss.Key + " near miss: " + nearMiss.Value);
            }
        }

        [Test]
        public void TheCoreAsmdefHasNoEngineAndNoAssemblyReferences()
        {
            JObject asmdef = JObject.Parse(File.ReadAllText(Path.Combine(CoreRoot, "PingCore.Core.asmdef")));
            Assert.That((string)asmdef["name"], Is.EqualTo("PingCore.Core"));
            Assert.That((bool?)asmdef["noEngineReferences"], Is.True);
            Assert.That(((JArray)asmdef["references"]).Count, Is.EqualTo(0));
            Assert.That((bool?)asmdef["overrideReferences"], Is.True);
            Assert.That(((JArray)asmdef["precompiledReferences"]).Select(t => (string)t), Is.EqualTo(new[] { "Newtonsoft.Json.dll" }));
        }

        private static IEnumerable<string> Scan(string line)
        {
            return Bans.Where(b => b.Pattern.IsMatch(line)).Select(b => b.Name);
        }
    }
}
