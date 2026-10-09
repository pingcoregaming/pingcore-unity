using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>
    /// The build guard's policy tables. Token-shaped test values are assembled from fragments at
    /// run time so this source file itself never holds one.
    /// </summary>
    public sealed class BuildGuardPolicyTests
    {
        private const string Tail16 = "0123456789abcdef";
        private const string Tail15 = "0123456789abcde";

        // A synthetic public id tail (32 hex), not any real app.
        private const string SyntheticPublicIdTail = "00112233445566778899aabbccddeeff";

        private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pingcore-guard-project"));

        private static string Token(string prefix, string tail) => prefix + "_" + tail;

        // ---- reasons ----------------------------------------------------------------------

        [Test]
        public void EveryReasonHasItsStableCode()
        {
            Assert.That(BuildGuardReasons.ToCode(BuildGuardReason.EditorAssembly), Is.EqualTo("editor_assembly"));
            Assert.That(BuildGuardReasons.ToCode(BuildGuardReason.InstrumentationInPlainBuild), Is.EqualTo("instrumentation_in_plain_build"));
            Assert.That(BuildGuardReasons.ToCode(BuildGuardReason.SecretLiteral), Is.EqualTo("secret_literal"));
            Assert.That(BuildGuardReasons.ToCode(BuildGuardReason.OutputUnscannable), Is.EqualTo("output_unscannable"));
            Assert.That(BuildGuardReasons.ToCode(BuildGuardReason.ScanError), Is.EqualTo("scan_error"));
        }

        // ---- IsInsideProjectFolder and the scope ------------------------------------------------

        /// <summary>A project's rules as a studio would write them: a debug assembly family, its define and its own output folder.</summary>
        private static readonly BuildGuardRules Rules = new BuildGuardRules(new[] { "Studio.Debug." }, new[] { "STUDIO_DEBUG" }, "Builds/Instrumented");

        private static BuildGuardScope Instrumented => new BuildGuardScope(Rules, true);

        private static BuildGuardScope PlainWithRules => new BuildGuardScope(Rules, false);

        [TestCase("Builds/Instrumented/demo/Game.x86_64", true)]
        [TestCase("Builds/Instrumented/Game.x86_64", true)]
        [TestCase("Builds/GuardDemo/instrumented/Game.x86_64", false)]
        [TestCase("Builds/InstrumentedX/Game.x86_64", false)]
        [TestCase("Builds/Instrumented", false)]
        [TestCase("Builds/Instrumented/", false)]
        [TestCase("Builds/Instrumented/../Server/Game.x86_64", false)]
        [TestCase("Other/Builds/Instrumented/Game.x86_64", false)]
        [TestCase("", false)]
        public void AnInstrumentedOutputIsOnlyStrictlyInsideTheRulesFolder(string relativeOutput, bool expected)
        {
            Assert.That(BuildGuardPolicy.IsInsideProjectFolder(Root, relativeOutput, "Builds/Instrumented"), Is.EqualTo(expected));
            Assert.That(BuildGuardScope.For(Rules, Root, relativeOutput).IsInstrumentedOutput, Is.EqualTo(expected));
        }

        [Test]
        public void AnInstrumentedOutputAcceptsAnAbsolutePathInsideTheProject()
        {
            string absolute = Path.Combine(Root, "Builds", "Instrumented", "demo", "Game.x86_64");
            Assert.That(BuildGuardScope.For(Rules, Root, absolute).IsInstrumentedOutput, Is.True);
        }

        [Test]
        public void TheFolderOfAnotherProjectIsNotAnInstrumentedOutput()
        {
            string other = Path.Combine(Path.GetTempPath(), "another-project", "Builds", "Instrumented", "Game.x86_64");
            Assert.That(BuildGuardScope.For(Rules, Root, other).IsInstrumentedOutput, Is.False);
        }

        [Test]
        public void WithoutAnInstrumentedFolderNoOutputIsInstrumented()
        {
            var noFolder = new BuildGuardRules(new[] { "Studio.Debug." }, new[] { "STUDIO_DEBUG" }, null);
            Assert.That(BuildGuardScope.For(noFolder, Root, "Builds/Instrumented/Game.x86_64").IsInstrumentedOutput, Is.False);
            Assert.That(new BuildGuardScope(noFolder, true).IsInstrumentedOutput, Is.False, "a scope cannot claim an instrumented output the rules do not allow");
            Assert.That(BuildGuardScope.For(null, Root, "Builds/Instrumented/Game.x86_64").IsInstrumentedOutput, Is.False);
        }

        // ---- CheckAssemblies ----------------------------------------------------------------

        [TestCase("PingCore.Editor")]
        [TestCase("PingCore.Editor.dll")]
        [TestCase("PingCore.Editor.Tests.dll")]
        [TestCase("Game_Data/Managed/PingCore.Editor.dll")]
        public void EditorAssemblyIsRejectedEvenInAnInstrumentedOutput(string name)
        {
            foreach (BuildGuardScope scope in new[] { BuildGuardScope.Plain, PlainWithRules, Instrumented })
            {
                var findings = BuildGuardPolicy.CheckAssemblies(new[] { "UnityEngine.dll", name }, scope);
                Assert.That(findings.Count, Is.EqualTo(1));
                Assert.That(findings[0].Reason, Is.EqualTo(BuildGuardReason.EditorAssembly));
                Assert.That(findings[0].Code, Is.EqualTo("editor_assembly"));
            }
        }

        [Test]
        public void ARestrictedAssemblyIsRejectedInAPlainBuild()
        {
            var findings = BuildGuardPolicy.CheckAssemblies(new[] { "Studio.Debug.Client.dll", "Studio.Debug.Server" }, PlainWithRules);
            Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "instrumentation_in_plain_build", "instrumentation_in_plain_build" }));
            Assert.That(findings.Select(f => f.Location), Is.EqualTo(new[] { "Studio.Debug.Client", "Studio.Debug.Server" }));
            Assert.That(findings[0].Detail, Does.Contain("Builds/Instrumented/"));
        }

        [Test]
        public void ARestrictedAssemblyIsAcceptedInAnInstrumentedOutput()
        {
            Assert.That(BuildGuardPolicy.CheckAssemblies(new[] { "Studio.Debug.Client.dll", "Studio.Debug.Server.dll" }, Instrumented), Is.Empty);
        }

        [Test]
        public void ARestrictedAssemblyIsRejectedEverywhereWhenTheRulesNameNoInstrumentedFolder()
        {
            var noFolder = new BuildGuardRules(new[] { "Studio.Debug." }, null, null);
            var findings = BuildGuardPolicy.CheckAssemblies(new[] { "Studio.Debug.Client.dll" }, BuildGuardScope.For(noFolder, Root, "Builds/Instrumented/Game.exe"));
            Assert.That(findings.Single().Reason, Is.EqualTo(BuildGuardReason.InstrumentationInPlainBuild));
        }

        [Test]
        public void WithoutRulesNoAssemblyButTheEditorsIsRestricted()
        {
            Assert.That(BuildGuardPolicy.CheckAssemblies(new[] { "Studio.Debug.Client.dll", "Anything.dll" }, BuildGuardScope.Plain), Is.Empty);
            Assert.That(BuildGuardPolicy.CheckAssemblies(new[] { "Studio.Debug.Client.dll" }, null), Is.Empty, "null is the plain scope");
        }

        [Test]
        public void RuntimeSdkAssembliesAreAccepted()
        {
            var names = new[]
            {
                "PingCore.Core.dll", "PingCore.Discovery.Client.dll", "PingCore.Discovery.Host.dll", "PingCore.Fleet.dll",
                "PingCore.Netcode.NGO.dll", "Assembly-CSharp.dll", "Studio.DebugTools.dll", "Unity.Netcode.Runtime.dll",
            };
            Assert.That(BuildGuardPolicy.CheckAssemblies(names, PlainWithRules), Is.Empty, "only a name starting with a prefix counts");
        }

        // ---- CheckDefines -------------------------------------------------------------------

        [Test]
        public void AnInstrumentationDefineIsRejectedInAPlainBuild()
        {
            var findings = BuildGuardPolicy.CheckDefines(BuildGuardPolicy.SplitDefines("UNITY_SERVER; STUDIO_DEBUG ;FOO"), PlainWithRules);
            Assert.That(findings.Count, Is.EqualTo(1));
            Assert.That(findings[0].Reason, Is.EqualTo(BuildGuardReason.InstrumentationInPlainBuild));
            Assert.That(findings[0].Location, Is.EqualTo("STUDIO_DEBUG"));
        }

        [Test]
        public void AnInstrumentationDefineIsAcceptedInAnInstrumentedOutput()
        {
            Assert.That(BuildGuardPolicy.CheckDefines(new[] { "STUDIO_DEBUG" }, Instrumented), Is.Empty);
        }

        [Test]
        public void DefinesThatOnlyContainTheInstrumentationNameAreAccepted()
        {
            Assert.That(BuildGuardPolicy.CheckDefines(new[] { "STUDIO_DEBUG_TOOLS", "NOT_STUDIO_DEBUG", "" }, PlainWithRules), Is.Empty);
            Assert.That(BuildGuardPolicy.CheckDefines(new[] { "STUDIO_DEBUG" }, BuildGuardScope.Plain), Is.Empty, "without rules no define is restricted");
        }

        // ---- ScanText -----------------------------------------------------------------------

        [TestCase("usr")]
        [TestCase("sys")]
        [TestCase("cdnpush")]
        [TestCase("dsc")]
        public void EveryPrefixWithA16CharacterTailIsASecretLiteral(string prefix)
        {
            string text = "const string Key = \"" + Token(prefix, Tail16) + "\";";
            BuildGuardScanResult result = BuildGuardPolicy.ScanText(text, "Assets/Key.cs");
            Assert.That(result.Findings.Count, Is.EqualTo(1));
            Assert.That(result.Findings[0].Reason, Is.EqualTo(BuildGuardReason.SecretLiteral));
            Assert.That(result.Findings[0].Code, Is.EqualTo("secret_literal"));
        }

        [TestCase("usr")]
        [TestCase("sys")]
        [TestCase("cdnpush")]
        [TestCase("dsc")]
        public void A15CharacterTailIsNotASecretLiteral(string prefix)
        {
            Assert.That(BuildGuardPolicy.ScanText("x = \"" + Token(prefix, Tail15) + "\"", "a").Findings, Is.Empty);
        }

        [Test]
        public void APublicDscpAppIdIsNotASecretLiteral()
        {
            string publicId = "dscp" + "_" + SyntheticPublicIdTail;
            BuildGuardScanResult result = BuildGuardPolicy.ScanText("appId: " + publicId, "Assets/Settings.asset");
            Assert.That(result.Findings, Is.Empty);
            Assert.That(result.AllowedDscTokenHits, Is.EqualTo(0));
        }

        [TestCase("x")]
        [TestCase("Z")]
        [TestCase("7")]
        public void APrefixPrecededByALetterOrDigitIsNotASecretLiteral(string before)
        {
            Assert.That(BuildGuardPolicy.ScanText(before + Token("usr", Tail16), "a").Findings, Is.Empty);
        }

        [TestCase("_")]
        [TestCase("\"")]
        [TestCase("/")]
        [TestCase("=")]
        public void APrefixPrecededByAPunctuationCharacterIsASecretLiteral(string before)
        {
            Assert.That(BuildGuardPolicy.ScanText(before + Token("usr", Tail16), "a").Findings.Count, Is.EqualTo(1));
        }

        [Test]
        public void ABarePrefixAndAnUppercasePrefixAreNotSecretLiterals()
        {
            Assert.That(BuildGuardPolicy.ScanText("prefix usr_ and USR_" + Tail16 + Tail16, "a").Findings, Is.Empty);
        }

        [Test]
        public void AConfirmedHeartbeatTokenIsAllowedAndCounted()
        {
            string heartbeat = Token("dsc", "open" + Tail16);
            BuildGuardScanResult result = BuildGuardPolicy.ScanText("openRegistrationHeartbeatToken: " + heartbeat, "Assets/S.asset",
                new[] { heartbeat });
            Assert.That(result.Findings, Is.Empty);
            Assert.That(result.AllowedDscTokenHits, Is.EqualTo(1));
        }

        [Test]
        public void ADifferentDscTokenIsRejectedEvenWhenAHeartbeatTokenIsConfirmed()
        {
            string heartbeat = Token("dsc", "open" + Tail16);
            string other = Token("dsc", "private" + Tail16);
            BuildGuardScanResult result = BuildGuardPolicy.ScanText(heartbeat + " " + other, "a", new[] { heartbeat });
            Assert.That(result.Findings.Count, Is.EqualTo(1));
            Assert.That(result.AllowedDscTokenHits, Is.EqualTo(1));
        }

        [Test]
        public void AHeartbeatTokenThatIsOnlyAPrefixOfTheHitDoesNotAllowIt()
        {
            string heartbeat = Token("dsc", "open" + Tail16);
            BuildGuardScanResult result = BuildGuardPolicy.ScanText(heartbeat + "EXTRA", "a", new[] { heartbeat });
            Assert.That(result.Findings.Count, Is.EqualTo(1));
            Assert.That(result.AllowedDscTokenHits, Is.EqualTo(0));
        }

        [Test]
        public void TheHeartbeatAllowanceNeverAppliesToAUsrToken()
        {
            string usr = Token("usr", Tail16);
            BuildGuardScanResult result = BuildGuardPolicy.ScanText(usr, "a", new[] { usr });
            Assert.That(result.Findings.Count, Is.EqualTo(1));
            Assert.That(result.AllowedDscTokenHits, Is.EqualTo(0));
        }

        [Test]
        public void AFindingNeverContainsTheMatchedToken()
        {
            string token = Token("usr", "SECRETTAIL" + Tail16);
            BuildGuardFinding finding = BuildGuardPolicy.ScanText("a\nb " + token, "Assets/K.cs").Findings.Single();
            Assert.That(finding.ToString(), Does.Not.Contain("SECRETTAIL"));
            Assert.That(finding.Detail, Does.Contain("usr_"));
            Assert.That(finding.Detail, Does.Contain("line 2"));
            Assert.That(finding.Location, Is.EqualTo("Assets/K.cs"));
        }

        // ---- ScanBytes ----------------------------------------------------------------------

        [Test]
        public void ScanBytesFindsAUtf8Token()
        {
            byte[] data = Encoding.UTF8.GetBytes("éé " + Token("cdnpush", Tail16) + " end");
            BuildGuardScanResult result = BuildGuardPolicy.ScanBytes(data, "f");
            Assert.That(result.Findings.Count, Is.EqualTo(1));
            Assert.That(result.Findings[0].Detail, Does.Contain("UTF-8"));
        }

        [Test]
        public void ScanBytesFindsAUtf16LeTokenAsInADotNetStringHeap()
        {
            byte[] data = new byte[] { 0x01, 0x02, 0x03 }
                .Concat(Encoding.Unicode.GetBytes(Token("usr", "0000guardcanary000000000000")))
                .Concat(new byte[] { 0x00, 0x00, 0xFF })
                .ToArray();
            BuildGuardScanResult result = BuildGuardPolicy.ScanBytes(data, "Game_Data/Managed/Assembly-CSharp.dll");
            Assert.That(result.Findings.Count, Is.EqualTo(1));
            Assert.That(result.Findings[0].Reason, Is.EqualTo(BuildGuardReason.SecretLiteral));
            Assert.That(result.Findings[0].Detail, Does.Contain("UTF-16LE"));
            Assert.That(result.Findings[0].Detail, Does.Contain("(31 chars)"));
        }

        [Test]
        public void ScanBytesFindsAUtf16LeTokenAtAnOddOffset()
        {
            byte[] data = new byte[] { 0x07 }.Concat(Encoding.Unicode.GetBytes(Token("sys", Tail16))).ToArray();
            Assert.That(BuildGuardPolicy.ScanBytes(data, "f").Findings.Count, Is.EqualTo(1));
        }

        [Test]
        public void ScanBytesIgnoresAUtf16LeTokenWithA15CharacterTail()
        {
            Assert.That(BuildGuardPolicy.ScanBytes(Encoding.Unicode.GetBytes(Token("usr", Tail15)), "f").Findings, Is.Empty);
        }

        [Test]
        public void ScanBytesIgnoresAUtf16LeDscpAppId()
        {
            byte[] data = Encoding.Unicode.GetBytes("dscp" + "_" + SyntheticPublicIdTail);
            Assert.That(BuildGuardPolicy.ScanBytes(data, "f").Findings, Is.Empty);
        }

        [Test]
        public void ScanBytesAllowsAConfirmedHeartbeatTokenInBothEncodings()
        {
            string heartbeat = Token("dsc", "open" + Tail16);
            byte[] data = Encoding.UTF8.GetBytes(heartbeat + " ").Concat(Encoding.Unicode.GetBytes(heartbeat)).ToArray();
            BuildGuardScanResult result = BuildGuardPolicy.ScanBytes(data, "f", new[] { heartbeat });
            Assert.That(result.Findings, Is.Empty);
            Assert.That(result.AllowedDscTokenHits, Is.EqualTo(2));
        }

        [Test]
        public void ScanBytesRespectsThePrecedingCharacterRuleInUtf16Le()
        {
            Assert.That(BuildGuardPolicy.ScanBytes(Encoding.Unicode.GetBytes("a" + Token("usr", Tail16)), "f").Findings, Is.Empty);
            Assert.That(BuildGuardPolicy.ScanBytes(Encoding.Unicode.GetBytes("-" + Token("usr", Tail16)), "f").Findings.Count, Is.EqualTo(1));
        }

        // "%" stands for "_" so the source holds no token-shaped string.
        [TestCase("plain text with no token at all", 0)]
        [TestCase("usr% sys% cdnpush% dsc%", 0)]
        [TestCase("k=usr%0123456789abcdef%more; dsc%0123456789abcdefXYZ", 2)]
        [TestCase("xdsc%0123456789abcdef9 cdnpush%ABCDEFGHIJKLMNOPQ", 1)]
        public void ScanBytesAgreesWithTheTokenPatternOnUtf8Text(string template, int expected)
        {
            string text = template.Replace('%', '_');
            Assert.That(BuildGuardPolicy.ScanText(text, "t").Findings.Count, Is.EqualTo(expected));
            int byPattern = BuildGuardPolicy.ScanText(text, "t").Findings.Count;
            int byBytes = BuildGuardPolicy.ScanBytes(Encoding.UTF8.GetBytes(text), "t").Findings.Count;
            Assert.That(byBytes, Is.EqualTo(byPattern));
        }

        [Test]
        public void ScanBytesOfEmptyInputHasNoFindings()
        {
            Assert.That(BuildGuardPolicy.ScanBytes(new byte[0], "f").Findings, Is.Empty);
            Assert.That(BuildGuardPolicy.ScanBytes(null, "f").Findings, Is.Empty);
        }
    }
}
