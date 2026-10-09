using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>
    /// The postprocessor's output evaluation on a synthetic build written beside unrelated files, as a
    /// build to the Desktop would be. Token-shaped values are assembled from fragments.
    /// </summary>
    public sealed class BuildOutputScannerTests
    {
        private static readonly string UsrToken = "usr" + "_" + "0000guardtest0000000000";

        private string project;
        private string desktop;
        private string output;

        [SetUp]
        public void SetUp()
        {
            string id = Guid.NewGuid().ToString("N");
            project = Path.Combine(Path.GetTempPath(), "pingcore-scan-project-" + id);
            desktop = Path.Combine(Path.GetTempPath(), "pingcore-scan-desktop-" + id);
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(Path.Combine(desktop, "Game_Data", "Managed"));
            output = Path.Combine(desktop, "Game.x86_64");

            Write("Game.x86_64", "ELF player");
            Write("UnityPlayer.so", "runtime");
            Write("Game_Data/ScriptingAssemblies.json", "{\"names\":[\"Assembly-CSharp.dll\",\"Studio.Debug.Server.dll\"]}");
            Write("Game_Data/level0", "scene data");

            // Not produced by the build, and each holds a token. The last three share the executable's
            // stem, which the old <stem>.*, <stem>_* and bare <stem> globs would have scanned and deleted.
            Write(".env", "PINGCORE_KEY=" + UsrToken);
            Write("notes.txt", "key " + UsrToken);
            Write("OtherGame_Data/level0", UsrToken);
            Write("Game.env", "PINGCORE_KEY=" + UsrToken);
            Write("Game_secrets.txt", UsrToken);
            Write("Game/config.txt", UsrToken);
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

        /// <summary>The rules of a project that keeps its debug assemblies out of plain builds.</summary>
        private static readonly BuildGuardRules Rules = new BuildGuardRules(new[] { "Studio.Debug." }, new[] { "STUDIO_DEBUG" }, "Builds/Instrumented");

        private BuildGuardOutputEvaluation Evaluate(bool instrumented = true, string[] reportFiles = null, bool sourceScanRan = false) =>
            BuildGuardPostprocessor.Evaluate(project, output, new BuildGuardScope(Rules, instrumented), Array.Empty<string>(), reportFiles, sourceScanRan);

        [Test]
        public void UnrelatedFilesBesideTheBuildAreNeverScanned()
        {
            BuildGuardOutputEvaluation evaluation = Evaluate();

            Assert.That(evaluation.Findings, Is.Empty);
            Assert.That(evaluation.FlaggedFiles, Is.Empty);
            Assert.That(evaluation.ScannedFiles, Is.EqualTo(4));
            Assert.That(evaluation.Coverage.OutputScan, Is.EqualTo("complete"));
        }

        [Test]
        public void ATokenInAProducedFileIsFoundAndOnlyThatFileIsFlagged()
        {
            Write("Game_Data/level0", "scene " + UsrToken);
            BuildGuardOutputEvaluation evaluation = Evaluate();

            Assert.That(evaluation.Findings.Select(f => f.Code), Is.EqualTo(new[] { "secret_literal" }));
            Assert.That(evaluation.FlaggedFiles, Is.EqualTo(new[] { BuildGuardPolicy.NormalizeFullPath(Path.Combine(desktop, "Game_Data", "level0")) }));
        }

        [Test]
        public void AReportFileInsideTheBuildRootIsScannedAndOneOutsideItIsNot()
        {
            Write("lib_extra_runtime.so", UsrToken);
            string outside = Path.Combine(project, "outside.txt");
            File.WriteAllText(outside, UsrToken);

            BuildGuardOutputEvaluation evaluation = Evaluate(reportFiles: new[] { Path.Combine(desktop, "lib_extra_runtime.so"), outside });

            Assert.That(evaluation.Findings.Count, Is.EqualTo(1));
            Assert.That(evaluation.FlaggedFiles, Is.EqualTo(new[] { BuildGuardPolicy.NormalizeFullPath(Path.Combine(desktop, "lib_extra_runtime.so")) }));
        }

        [Test]
        public void ARestrictedAssemblyInTheShippedListIsRejectedInAPlainBuild()
        {
            BuildGuardOutputEvaluation evaluation = Evaluate(instrumented: false);

            Assert.That(evaluation.AssemblySource, Is.EqualTo("ScriptingAssemblies.json"));
            Assert.That(evaluation.Findings.Select(f => f.Code + " " + f.Location), Is.EqualTo(new[] { "instrumentation_in_plain_build Studio.Debug.Server" }));
        }

        [Test]
        public void DeletingARejectedBuildRemovesOnlyWhatTheBuildProduced()
        {
            BuildOutputScanner.DeleteProduced(project, BuildGuardPolicy.NormalizeFullPath(output), BuildOutputScanner.ProducedPaths(output, null));

            Assert.That(File.Exists(Path.Combine(desktop, "Game.x86_64")), Is.False);
            Assert.That(File.Exists(Path.Combine(desktop, "UnityPlayer.so")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(desktop, "Game_Data")), Is.False);
            Assert.That(File.Exists(Path.Combine(desktop, ".env")), Is.True);
            Assert.That(File.Exists(Path.Combine(desktop, "notes.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(desktop, "OtherGame_Data", "level0")), Is.True);
            Assert.That(File.Exists(Path.Combine(desktop, "Game.env")), Is.True);
            Assert.That(File.Exists(Path.Combine(desktop, "Game_secrets.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(desktop, "Game", "config.txt")), Is.True);
        }

        [Test]
        public void FilesAndAFolderThatOnlyShareTheStemAreNeitherProducedNorScanned()
        {
            var produced = BuildOutputScanner.ProducedPaths(output, null, "Product").Select(p => Path.GetFileName(p)).ToArray();
            Assert.That(produced, Is.EquivalentTo(new[] { "Game.x86_64", "UnityPlayer.so", "Game_Data" }));

            BuildGuardOutputEvaluation evaluation = Evaluate();
            Assert.That(evaluation.Findings, Is.Empty, "Game.env, Game_secrets.txt and Game/ each hold a token");
            Assert.That(evaluation.ScannedFiles, Is.EqualTo(4));
        }

        [Test]
        public void ACompressedPlayerDataFileWithoutTheSourceSideScanFailsAsOutputUnscannable()
        {
            Write("Game_Data/data.unity3d", "compressed");
            BuildGuardOutputEvaluation evaluation = Evaluate(sourceScanRan: false);

            Assert.That(evaluation.Findings.Select(f => f.Code), Is.EqualTo(new[] { "output_unscannable" }));
            Assert.That(evaluation.Coverage.OutputScan, Is.EqualTo("unavailable"));
        }

        [Test]
        public void ACompressedPlayerDataFileWithTheSourceSideScanButNoStagedAssemblyFailsClosed()
        {
            // The temporary project has no Library/ staging and the report lists no .dll.
            Write("Game_Data/data.unity3d", "compressed");
            BuildGuardOutputEvaluation evaluation = Evaluate(sourceScanRan: true);

            Assert.That(evaluation.StagedAssembliesScanned, Is.EqualTo(0));
            Assert.That(evaluation.Findings.Select(f => f.Code), Is.EqualTo(new[] { "output_unscannable" }));
            Assert.That(evaluation.Findings[0].Detail, Does.Contain("no compiled player assembly was found"));
            Assert.That(evaluation.Coverage.OutputScan, Is.EqualTo("unavailable"));
        }

        [Test]
        public void AnOutputWithNoAssemblyListAndNoSourceSideScanFailsClosed()
        {
            File.Delete(Path.Combine(desktop, "Game_Data", "ScriptingAssemblies.json"));
            BuildGuardOutputEvaluation evaluation = Evaluate(sourceScanRan: false);

            Assert.That(evaluation.AssemblyCount, Is.EqualTo(0));
            Assert.That(evaluation.Findings.Select(f => f.Code), Is.EqualTo(new[] { "output_unscannable" }));
        }
    }
}
