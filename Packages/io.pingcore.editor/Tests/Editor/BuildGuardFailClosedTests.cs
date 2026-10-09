using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;

namespace PingCore.Editor.Tests
{
    /// <summary>
    /// A postprocess check that cannot finish, for any reason, fails the build closed: the produced
    /// output is deleted, the verdict is <c>fail</c> with <c>scan_error</c> naming the exception's type
    /// (never its message), and a <see cref="BuildFailedException"/> is thrown. Each case injects the
    /// failure through <see cref="BuildGuardPostprocessInputs"/> or a real malformed file.
    /// </summary>
    public sealed class BuildGuardFailClosedTests
    {
        private static readonly string UsrToken = "usr" + "_" + "0000failclosed0000000000";

        private string project;
        private string desktop;
        private string output;

        [SetUp]
        public void SetUp()
        {
            string id = Guid.NewGuid().ToString("N");
            project = Path.Combine(Path.GetTempPath(), "pingcore-failclosed-project-" + id);
            desktop = Path.Combine(Path.GetTempPath(), "pingcore-failclosed-desktop-" + id);
            output = Path.Combine(desktop, "Game.x86_64");
            Directory.CreateDirectory(project);
            Write("Game.x86_64", "ELF player");
            Write("Game_Data/ScriptingAssemblies.json", "{\"names\":[\"Assembly-CSharp.dll\"]}");
            Write("Game_Data/level0", "scene data");
            Write(".env", "PINGCORE_KEY=" + UsrToken);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string dir in new[] { project, desktop })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        private void Write(string relative, string text)
        {
            string path = Path.Combine(desktop, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private BuildGuardSourceScan CleanSourceScan() =>
            BuildGuardSourceStage.Scan(project, () => BuildGuardTokenResolution.None, _ => { });

        private BuildGuardPostprocessInputs Inputs(Func<BuildGuardSourceScan> sourceScan = null, Func<IReadOnlyList<string>> reportFiles = null,
            Func<BuildGuardRules> rules = null) =>
            new BuildGuardPostprocessInputs(project, output, reportFiles ?? (() => Array.Empty<string>()), () => Array.Empty<string>(),
                () => new[] { "Assembly-CSharp" }, rules ?? (() => BuildGuardRules.Read(project)), sourceScan ?? CleanSourceScan);

        /// <summary>The guard logs its scan_error once as an error; the test expects exactly that line.</summary>
        private static void ExpectScanErrorLog() => LogAssert.Expect(LogType.Error, new Regex(@"^\[PingCore build guard\] scan_error: "));

        private void AssertFailedClosed(BuildFailedException thrown, string stage, string exceptionType)
        {
            Assert.That(thrown, Is.Not.Null);
            BuildGuardVerdict verdict = BuildGuardVerdictFile.Read(project);
            Assert.That(verdict.verdict, Is.EqualTo("fail"));
            Assert.That(verdict.stage, Is.EqualTo(stage));
            Assert.That(verdict.reasons, Is.EqualTo(new[] { "scan_error" }));
            Assert.That(verdict.findings[0].detail, Does.Contain("(" + exceptionType + ")"));
            Assert.That(thrown.Message, Does.Contain("scan_error"));

            string verdictText = File.ReadAllText(BuildGuardVerdictFile.GetPath(project));
            Assert.That(verdictText.Contains(UsrToken) || thrown.Message.Contains(UsrToken), Is.False, "a token in the exception message never reaches the verdict");

            Assert.That(File.Exists(output), Is.False, "the produced executable is deleted");
            Assert.That(Directory.Exists(Path.Combine(desktop, "Game_Data")), Is.False, "the produced data folder is deleted");
            Assert.That(File.Exists(Path.Combine(desktop, ".env")), Is.True, "nothing else beside the build is touched");
        }

        [Test]
        public void ACleanBuildStillPasses()
        {
            BuildGuardPostprocessor.Run(Inputs());

            Assert.That(BuildGuardVerdictFile.Read(project).verdict, Is.EqualTo("pass"));
            Assert.That(File.Exists(output), Is.True);
        }

        [Test]
        public void AMalformedShippedAssemblyListFailsClosedWithScanError()
        {
            // JsonUtility throws on this; before, only unreadable files failed closed and this left the output with a pending verdict.
            Write("Game_Data/ScriptingAssemblies.json", "{ \"names\": [ not json");
            ExpectScanErrorLog();
            var thrown = Assert.Throws<BuildFailedException>(() => BuildGuardPostprocessor.Run(Inputs()));

            Assert.That(BuildGuardVerdictFile.Read(project).findings[0].detail, Does.Match(@"could not finish its scan \(\w+Exception\)"));
            AssertFailedClosed(thrown, "postprocess", "ArgumentException");
        }

        [Test]
        public void AMalformedRulesFileFailsClosedWithScanErrorAndDeletesTheOutput()
        {
            // A typo must never switch a rule off quietly: an unknown key is a problem, not an ignored field.
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            File.WriteAllText(Path.Combine(project, BuildGuardRules.RelativePath), "{\"restrictedAssemblyPrefix\": [\"Studio.Debug.\"]}");
            ExpectScanErrorLog();
            var thrown = Assert.Throws<BuildFailedException>(() => BuildGuardPostprocessor.Run(Inputs()));

            // The detail names the file and the problem in plain words, not just an exception type, so a studio can fix the typo.
            string named = BuildGuardRules.RelativePath + ": unknown key \"restrictedAssemblyPrefix\"";
            Assert.That(BuildGuardVerdictFile.Read(project).findings[0].detail, Is.EqualTo(named), "[mutation: name only FormatException]");
            Assert.That(thrown.Message, Does.Contain(named));
            Assert.That(File.ReadAllText(BuildGuardVerdictFile.GetPath(project)), Does.Not.Contain("Studio.Debug."), "no value from the file reaches the verdict");
            Assert.That(File.Exists(output), Is.False, "the produced executable is deleted");
        }

        [Test]
        public void ASourceScanThatThrowsAnythingFailsClosedNamingOnlyTheExceptionType()
        {
            BuildGuardSourceScan Throwing() => BuildGuardSourceStage.Scan(project, () => BuildGuardTokenResolution.None,
                _ => throw new InvalidDataException("folder listing failed near " + UsrToken));
            ExpectScanErrorLog();
            var thrown = Assert.Throws<BuildFailedException>(() => BuildGuardPostprocessor.Run(Inputs(Throwing)));

            AssertFailedClosed(thrown, "postprocess", "InvalidDataException");
        }

        [Test]
        public void ATokenResolutionThatThrowsIsCaughtByTheSourceScan()
        {
            BuildGuardSourceScan scan = BuildGuardSourceStage.Scan(project, () => throw new FormatException("bad confirmation file"), _ => { });

            Assert.That(scan.Error, Is.TypeOf<FormatException>());
            Assert.That(scan.Scanner, Is.Not.Null);
        }

        [Test]
        public void TheSourceStageFailsClosedOnItsOwnWhenItsScanCouldNotFinish()
        {
            BuildGuardSourceScan scan = BuildGuardSourceStage.Scan(project, () => BuildGuardTokenResolution.None,
                _ => throw new UnauthorizedAccessException(UsrToken));
            ExpectScanErrorLog();
            var thrown = Assert.Throws<BuildFailedException>(() => BuildGuardSourceStage.FailIfIncomplete(project, output, null, scan));

            AssertFailedClosed(thrown, "source", "UnauthorizedAccessException");
        }

        [Test]
        public void AReportFileListThatThrowsStillFailsClosedAndDeletesTheOutput()
        {
            ExpectScanErrorLog();
            var thrown = Assert.Throws<BuildFailedException>(() =>
                BuildGuardPostprocessor.Run(Inputs(reportFiles: () => throw new IOException("report unreadable"))));

            AssertFailedClosed(thrown, "postprocess", "IOException");
        }

        [Test]
        public void AnUnreadableFileKeepsTheGuardsOwnMessageAndStillCountsAsScanError()
        {
            BuildGuardSourceScan Unreadable() => BuildGuardSourceStage.Scan(project, () => BuildGuardTokenResolution.None,
                scanner => scanner.ScanSourceCode(Path.Combine(project, "Assets", "Missing.cs")));
            ExpectScanErrorLog();
            var thrown = Assert.Throws<BuildFailedException>(() => BuildGuardPostprocessor.Run(Inputs(Unreadable)));

            Assert.That(BuildGuardVerdictFile.Read(project).findings[0].detail, Does.Contain("could not read"));
            Assert.That(BuildGuardVerdictFile.Read(project).reasons, Is.EqualTo(new[] { "scan_error" }));
            Assert.That(File.Exists(output), Is.False);
            Assert.That(thrown.Message, Does.Contain("scan_error"));
        }
    }
}
