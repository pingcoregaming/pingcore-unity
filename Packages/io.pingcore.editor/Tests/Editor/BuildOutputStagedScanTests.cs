using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>
    /// When the output hides part of itself (here compressed player data), the guard byte-scans the
    /// compiled player assemblies from the build's staging, so a constant-folded token that only exists
    /// in IL is still caught. Token-shaped values are assembled from fragments.
    /// </summary>
    public sealed class BuildOutputStagedScanTests
    {
        private static readonly string FoldedToken = "usr" + "_" + "0000guardfolded000000000";

        private string project;
        private string desktop;
        private string output;

        [SetUp]
        public void SetUp()
        {
            string id = Guid.NewGuid().ToString("N");
            project = Path.Combine(Path.GetTempPath(), "pingcore-staged-project-" + id);
            desktop = Path.Combine(Path.GetTempPath(), "pingcore-staged-desktop-" + id);
            output = Path.Combine(desktop, "Game.apk");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(desktop);
            File.WriteAllText(output, "an archive the byte scan cannot see inside");
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

        /// <summary>A fake assembly whose string heap holds <paramref name="literal"/> as UTF-16LE, as a compiled literal is.</summary>
        private static void WriteAssembly(string path, string literal)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] header = Encoding.ASCII.GetBytes("MZ fake assembly ");
            File.WriteAllBytes(path, header.Concat(Encoding.Unicode.GetBytes(literal)).ToArray());
        }

        private string Staged(string name) => Path.Combine(project, "Library", "Bee", "PlayerScriptAssemblies", name);

        private BuildGuardOutputEvaluation Evaluate(string[] reportFiles = null) =>
            BuildGuardPostprocessor.Evaluate(project, output, BuildGuardScope.Plain, Array.Empty<string>(), reportFiles, true);

        [Test]
        public void AFoldedTokenInAStagedAssemblyFailsAnArchiveBuild()
        {
            WriteAssembly(Staged("Assembly-CSharp.dll"), "Value=" + FoldedToken);
            BuildGuardOutputEvaluation evaluation = Evaluate();

            Assert.That(evaluation.StagedAssembliesScanned, Is.EqualTo(1));
            Assert.That(evaluation.Findings.Select(f => f.Code + " " + f.Location),
                Is.EqualTo(new[] { "secret_literal Library/Bee/PlayerScriptAssemblies/Assembly-CSharp.dll" }));
            Assert.That(evaluation.Findings[0].Detail, Does.Contain("UTF-16LE"));
            Assert.That(evaluation.FlaggedFiles, Is.Empty, "a staged assembly is not a produced output path, so it is never deleted");
        }

        [Test]
        public void ACleanStagedAssemblyLetsAnArchiveBuildPassAsAssembliesOnly()
        {
            WriteAssembly(Staged("Assembly-CSharp.dll"), "Value=nothing secret here");
            BuildGuardOutputEvaluation evaluation = Evaluate();

            Assert.That(evaluation.Findings, Is.Empty);
            Assert.That(evaluation.Coverage.OutputScan, Is.EqualTo("assemblies-only"));
            Assert.That(evaluation.Coverage.Notes.Any(n => n.Contains("Game.apk")), Is.True);
        }

        [Test]
        public void AssembliesTheReportListsOutsideTheBuildRootAreScannedInsteadOfTheStagingFolder()
        {
            string reported = Path.Combine(project, "Temp", "StagingArea", "Data", "Managed", "Assembly-CSharp.dll");
            WriteAssembly(reported, FoldedToken);
            WriteAssembly(Staged("Assembly-CSharp.dll"), FoldedToken);
            BuildGuardOutputEvaluation evaluation = Evaluate(new[] { reported, Path.Combine(project, "Temp", "StagingArea", "notes.txt") });

            Assert.That(evaluation.StagedAssembliesScanned, Is.EqualTo(1));
            Assert.That(evaluation.Findings.Single().Location, Is.EqualTo("Temp/StagingArea/Data/Managed/Assembly-CSharp.dll"));
            Assert.That(File.Exists(reported), Is.True, "read-only: an assembly outside the build root is never deleted");
        }

        [Test]
        public void TheIl2CppMetadataIsScannedToo()
        {
            WriteAssembly(Staged("Assembly-CSharp.dll"), "clean");
            string metadata = Path.Combine(project, "Library", "Bee", "artifacts", "LinuxPlayerBuildProgram", "il2cppOutput", "data", "Metadata",
                "global-metadata.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(metadata));
            File.WriteAllBytes(metadata, Encoding.UTF8.GetBytes("strings " + FoldedToken));
            BuildGuardOutputEvaluation evaluation = Evaluate();

            Assert.That(evaluation.StagedAssembliesScanned, Is.EqualTo(2));
            Assert.That(evaluation.Findings.Single().Location, Does.EndWith("Metadata/global-metadata.dat"));
        }

        [Test]
        public void ACompleteOutputScanNeverReadsTheStagingFolder()
        {
            File.Delete(output);
            output = Path.Combine(desktop, "Game.x86_64");
            File.WriteAllText(output, "ELF player");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(desktop, "Game_Data")).FullName, "ScriptingAssemblies.json"),
                "{\"names\":[\"Assembly-CSharp.dll\"]}");
            WriteAssembly(Staged("Assembly-CSharp.dll"), FoldedToken);
            BuildGuardOutputEvaluation evaluation = Evaluate();

            Assert.That(evaluation.Coverage.OutputScan, Is.EqualTo("complete"));
            Assert.That(evaluation.StagedAssembliesScanned, Is.EqualTo(0));
            Assert.That(evaluation.Findings, Is.Empty);
        }

        // ---- the pure selection ------------------------------------------------------------

        [Test]
        public void TheSelectionPrefersReportAssembliesAndAlwaysAddsIl2CppMetadata()
        {
            string root = Path.Combine(Path.GetTempPath(), "pingcore-select");
            string reportDll = Path.Combine(root, "Out", "Managed", "Game.dll");
            string stagingDll = Path.Combine(root, "Library", "Bee", "PlayerScriptAssemblies", "Assembly-CSharp.dll");
            string metadata = Path.Combine(root, "Library", "Bee", "artifacts", "x", "global-metadata.dat");
            string N(string p) => BuildGuardPolicy.NormalizeFullPath(p);

            Assert.That(BuildGuardStagedAssemblies.Select(new[] { reportDll, reportDll, Path.Combine(root, "Out", "level0") }, new[] { stagingDll }, new[] { metadata }),
                Is.EqualTo(new[] { N(reportDll), N(metadata) }), "report .dll files win; duplicates and non-assemblies are dropped");
            Assert.That(BuildGuardStagedAssemblies.Select(new[] { Path.Combine(root, "Out", "Game.apk") }, new[] { stagingDll, stagingDll + ".mdb" }, null),
                Is.EqualTo(new[] { N(stagingDll) }), "with no report .dll the staging assemblies are used");
            Assert.That(BuildGuardStagedAssemblies.Select(null, null, new[] { Path.Combine(root, "other.dat") }), Is.Empty,
                "nothing to scan means nothing vouches for the compiled code");
        }
    }
}
