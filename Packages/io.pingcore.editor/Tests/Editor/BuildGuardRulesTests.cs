using System;
using System.IO;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>The project's build guard rules file: what it accepts, and every way it is refused rather than half-read.</summary>
    public sealed class BuildGuardRulesTests
    {
        [Test]
        public void AFullRulesFileIsReadAsWritten()
        {
            BuildGuardRules rules = BuildGuardRules.Parse(
                "{\"restrictedAssemblyPrefixes\":[\"Studio.Debug.\",\"Studio.Cheats\"],\"instrumentationDefines\":[\"STUDIO_DEBUG\"],\"instrumentedOutputFolder\":\"Builds/Instrumented\"}");
            Assert.That(rules.RestrictedAssemblyPrefixes, Is.EqualTo(new[] { "Studio.Debug.", "Studio.Cheats" }));
            Assert.That(rules.InstrumentationDefines, Is.EqualTo(new[] { "STUDIO_DEBUG" }));
            Assert.That(rules.InstrumentedOutputFolder, Is.EqualTo("Builds/Instrumented"));
            Assert.That(rules.IsEmpty, Is.False);
        }

        [Test]
        public void EveryKeyIsOptionalAndAnEmptyFolderAllowsNoInstrumentedBuild()
        {
            BuildGuardRules rules = BuildGuardRules.Parse("{\"restrictedAssemblyPrefixes\":[\"Studio.Debug.\"],\"instrumentedOutputFolder\":\"\"}");
            Assert.That(rules.InstrumentationDefines, Is.Empty);
            Assert.That(rules.InstrumentedOutputFolder, Is.Null);
            Assert.That(BuildGuardRules.Parse("{}").IsEmpty, Is.True);
            Assert.That(BuildGuardRules.None.IsEmpty, Is.True);
        }

        [Test]
        public void CommentsAroundTheObjectAreAllowed()
        {
            BuildGuardRules rules = BuildGuardRules.Parse("// the debug tools\n{\"instrumentationDefines\":[\"STUDIO_DEBUG\"]} // end\n");
            Assert.That(rules.InstrumentationDefines, Is.EqualTo(new[] { "STUDIO_DEBUG" }));
        }

        [Test]
        public void ARepeatedEntryIsKeptOnce()
        {
            BuildGuardRules rules = BuildGuardRules.Parse("{\"instrumentationDefines\":[\"STUDIO_DEBUG\",\"STUDIO_DEBUG\"]}");
            Assert.That(rules.InstrumentationDefines, Is.EqualTo(new[] { "STUDIO_DEBUG" }));
        }

        [TestCase("", "the file is empty; it must hold one JSON object", TestName = "an empty file")]
        [TestCase("[]", "the file must hold one JSON object, and it starts with a list", TestName = "an array")]
        [TestCase("{\"restrictedAssemblyPrefixes\": [", "not valid JSON at line 1, position", TestName = "broken JSON")]
        [TestCase("{\"restrictedAssemblyPrefix\":[\"Studio.Debug.\"]}", "unknown key \"restrictedAssemblyPrefix\"", TestName = "a mistyped key")]
        [TestCase("{\"instrumentationDefines\":[\"STUDIO_DEBUG\"],\"instrumentationDefines\":[]}", "duplicate key \"instrumentationDefines\"", TestName = "a key given twice, the second one empty")]
        [TestCase("{\"instrumentationDefines\":[\"STUDIO_DEBUG\"]} {\"instrumentationDefines\":[]}", "trailing content after the object", TestName = "a second object after the first")]
        [TestCase("{\"instrumentationDefines\":[\"STUDIO_DEBUG\"]} x", "trailing content after the object", TestName = "text after the object")]
        [TestCase("{\"restrictedAssemblyPrefixes\":\"Studio.Debug.\"}", "restrictedAssemblyPrefixes must be a list of strings", TestName = "a string where a list belongs")]
        [TestCase("{\"instrumentationDefines\":{\"a\":1}}", "instrumentationDefines must be a list of strings", TestName = "an object where a list belongs")]
        [TestCase("{\"restrictedAssemblyPrefixes\":[\"\"]}", "not an assembly name prefix", TestName = "an empty prefix")]
        [TestCase("{\"restrictedAssemblyPrefixes\":[\"Studio Debug\"]}", "not an assembly name prefix", TestName = "a prefix with a space")]
        [TestCase("{\"restrictedAssemblyPrefixes\":[3]}", "not an assembly name prefix", TestName = "a number as a prefix")]
        [TestCase("{\"instrumentationDefines\":[\"STUDIO-DEBUG\"]}", "not a scripting define", TestName = "a define with a dash")]
        [TestCase("{\"instrumentedOutputFolder\":3}", "must be a string", TestName = "a number as the folder")]
        [TestCase("{\"instrumentedOutputFolder\":\"../Builds\"}", "must be a folder of the project", TestName = "a folder that climbs")]
        [TestCase("{\"instrumentedOutputFolder\":\"/tmp/x\"}", "must be a folder of the project", TestName = "a rooted folder")]
        [TestCase("{\"instrumentedOutputFolder\":\"C:/x\"}", "must be a folder of the project", TestName = "a drive")]
        [TestCase("{\"instrumentedOutputFolder\":\"Builds\\\\X\"}", "must be a folder of the project", TestName = "backslashes")]
        [TestCase("{\"instrumentedOutputFolder\":\"Builds/\"}", "must be a folder of the project", TestName = "a trailing slash")]
        [TestCase("{\"instrumentedOutputFolder\":\"Assets/Debug\"}", "must be a folder of the project", TestName = "inside Assets")]
        [TestCase("{\"instrumentedOutputFolder\":\"library/x\"}", "must be a folder of the project", TestName = "inside Library, any case")]
        public void AnythingUnexpectedRefusesTheWholeFile(string json, string expected)
        {
            var thrown = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse(json));
            Assert.That(thrown.Message, Does.Contain(expected));
            Assert.That(thrown.Message, Does.StartWith(BuildGuardRules.RelativePath + ": "), "the message names the file");
        }

        [TestCase("{\n  \"instrumentationDefines\": [\"A\" \"B\"]\n}", 2, TestName = "a missing comma on line 2")]
        [TestCase("{\"instrumentationDefines\": tru}", 1, TestName = "a misspelt literal on line 1")]
        [TestCase("{\n\n\n  \"instrumentedOutputFolder\": \"Builds/X\"\n", 5, TestName = "an object never closed")]
        public void InvalidJsonNamesTheLineAndPositionAndNothingFromTheFile(string json, int line)
        {
            var thrown = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse(json));
            Assert.That(thrown.Message, Does.Match("not valid JSON at line " + line + ", position [0-9]+"), "[mutation: report a fixed position]");
            Assert.That(thrown.Message, Does.Not.Contain("tru").And.Not.Contain("Builds/X").And.Not.Contain("\"A\""), "no text from the file");
        }

        [Test]
        public void AProblemQuotesOnlyAKeyNameNeverAValue()
        {
            // The folder, a prefix, a define and a value of the wrong type stay out of the message; only key names are quoted.
            string secretish = "usr" + "_" + new string('a', 20);
            var folder = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse("{\"instrumentedOutputFolder\":\"../" + secretish + "\"}"));
            Assert.That(folder.Message, Does.Contain("instrumentedOutputFolder must be a folder of the project").And.Not.Contain(secretish), "[mutation: quote the folder]");
            var list = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse("{\"restrictedAssemblyPrefixes\":\"" + secretish + "\"}"));
            Assert.That(list.Message, Does.Not.Contain(secretish));
            var define = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse("{\"instrumentationDefines\":[\"" + secretish + " x\"]}"));
            Assert.That(define.Message, Does.Contain("instrumentationDefines holds an entry that is not a scripting define").And.Not.Contain(secretish));
        }

        [TestCase("usr" + "_" + "zzzzzzzzzzzzzzzzzzzz", TestName = "a token-shaped key")] // built from fragments: no token-shaped literal in source
        [TestCase("a key with spaces", TestName = "a key with spaces")]
        [TestCase("", TestName = "an empty key")]
        public void AnUnknownKeyThatIsNotPlainIsDescribedNotQuoted(string key)
        {
            var unknown = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse("{\"" + key + "\":1}"));
            Assert.That(unknown.Message, Does.Contain("unknown key (a key name that is not shown"));
            if (key.Length > 0)
            {
                Assert.That(unknown.Message, Does.Not.Contain(key), "[mutation: quote every key]");
            }

            var twice = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse("{\"" + key + "\":1,\"" + key + "\":2}"));
            Assert.That(twice.Message, Does.Contain("duplicate key (a key name that is not shown"));
            Assert.That(BuildGuardRules.KeyName(new string('k', 41)), Does.StartWith("(a key name that is not shown"), "longer than 40 characters");
            Assert.That(BuildGuardRules.KeyName(new string('k', 40)), Is.EqualTo("\"" + new string('k', 40) + "\""));
        }

        [Test]
        public void AKeyShapedLikeAnUnprefixedCredentialIsNeverQuoted()
        {
            // Built at run time: no credential-shaped literal in source.
            string cdnToken = new string('a', 32) + new string('7', 32);
            string serverKey = "12345-" + new string('b', 32);
            string digestPart = "x" + new string('c', 12);
            foreach (string key in new[] { cdnToken, serverKey, digestPart, "a.b", "1abc" })
            {
                var thrown = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse("{\"" + key + "\":1}"));
                Assert.That(thrown.Message, Does.Contain("unknown key (a key name that is not shown").And.Not.Contain(key), key.Length + " characters [mutation: quote any key under 65 characters]");
            }

            Assert.That(BuildGuardRules.KeyName("restrictedAssemblyPrefix"), Is.EqualTo("\"restrictedAssemblyPrefix\""), "a plain mistyped key is still named");
            Assert.That(BuildGuardRules.KeyName("x" + new string('c', 11)), Is.EqualTo("\"x" + new string('c', 11) + "\""), "11 hex digits in a row are fine");
        }

        [Test]
        public void EveryProblemIsNamedNotOnlyTheFirst()
        {
            var thrown = Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Parse("{\"x\":1,\"instrumentationDefines\":[\"a-b\"],\"instrumentedOutputFolder\":\"..\"}"));
            Assert.That(thrown.Message, Does.Contain("unknown key \"x\"").And.Contain("not a scripting define").And.Contain("must be a folder of the project"));
        }

        [Test]
        public void AProjectWithoutTheFileHasNoRulesAndOneWithItHasThem()
        {
            string project = Path.Combine(Path.GetTempPath(), "pingcore-rules-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
                Assert.That(BuildGuardRules.Read(project), Is.SameAs(BuildGuardRules.None));
                File.WriteAllText(Path.Combine(project, BuildGuardRules.RelativePath), "{\"restrictedAssemblyPrefixes\":[\"Studio.Debug.\"]}");
                Assert.That(BuildGuardRules.Read(project).RestrictedAssemblyPrefixes, Is.EqualTo(new[] { "Studio.Debug." }));
                File.WriteAllText(Path.Combine(project, BuildGuardRules.RelativePath), "{\"restricted\":[]}");
                Assert.Throws<BuildGuardRulesException>(() => BuildGuardRules.Read(project), "a broken file is never read as no rules");
            }
            finally
            {
                Directory.Delete(project, true);
            }
        }
    }
}
