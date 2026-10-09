using NUnit.Framework;
using BeaconRush.Editor;
using UnityEditor;

namespace BeaconRush.Client.Tests
{
    /// <summary>The command line of <see cref="ClientBuild"/>, and the extra defines and output root a wrapping build adds.</summary>
    public sealed class ClientBuildArgsTests
    {
        [Test]
        public void TheDefaultsAreIl2CppAndThePlainOutput()
        {
            ClientBuildArgs args = ClientBuildArgs.Parse(new[] { "Unity.exe", "-batchmode", "-executeMethod", "BeaconRush.Editor.ClientBuild.Run", "-pingcoreVersion", "w5" });
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.Backend, Is.EqualTo(ScriptingImplementation.IL2CPP));
            Assert.That(args.OutputRoot, Is.EqualTo("Builds/Client"));
            Assert.That(args.ExecutablePath, Is.EqualTo("Builds/Client/w5/BeaconRushClient.exe"));
            Assert.That(args.VersionFilePath, Is.EqualTo("Builds/Client/w5/version.txt"));
            Assert.That(args.ExtraDefines, Is.Empty);
        }

        [Test]
        public void MonoChangesOnlyTheBackend()
        {
            ClientBuildArgs args = ClientBuildArgs.Parse(new[] { "-PINGCOREVERSION", "1.2.3-rc_1", "-pingcoreBackend", "mono" });
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.Backend, Is.EqualTo(ScriptingImplementation.Mono2x));
            Assert.That(args.BackendName, Is.EqualTo("Mono"));
            Assert.That(args.ExecutablePath, Is.EqualTo("Builds/Client/1.2.3-rc_1/BeaconRushClient.exe"));
            Assert.That(args.ExtraDefines, Is.Empty);
        }

        [Test]
        public void AWrappingBuildAddsDefinesAndAnotherOutputRoot()
        {
            ClientBuildArgs args = ClientBuildArgs.Parse(new[] { "-pingcoreVersion", "v1", "-pingcoreBackend", "Mono" })
                .WithInstrumentation(new[] { "STUDIO_DEBUG", "STUDIO_DEBUG", "MORE" }, "Builds/Instrumented/Client");
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.ExtraDefines, Is.EqualTo(new[] { "STUDIO_DEBUG", "MORE" }));
            Assert.That(args.ExecutablePath, Is.EqualTo("Builds/Instrumented/Client/v1/BeaconRushClient.exe"));
            Assert.That(args.Backend, Is.EqualTo(ScriptingImplementation.Mono2x), "everything else is kept");
        }

        [TestCase(new[] { "BAD-DEFINE" }, "Builds/X", "scripting define")]
        [TestCase(new[] { "OK" }, "Server", "under Builds/")]
        [TestCase(new[] { "OK" }, "Builds", "under Builds/")]
        [TestCase(new[] { "OK" }, "Builds/../Assets", "under Builds/")]
        [TestCase(new[] { "OK" }, null, "under Builds/")]
        public void AWrappingBuildWithABadDefineOrRootIsNotValid(string[] defines, string root, string expected)
        {
            ClientBuildArgs args = ClientBuildArgs.Parse(new[] { "-pingcoreVersion", "v1" }).WithInstrumentation(defines, root);
            Assert.That(args.IsValid, Is.False);
            Assert.That(string.Join("; ", args.Errors), Does.Contain(expected));
        }

        [TestCase(new string[0], "is required")]
        [TestCase(new[] { "-pingcoreVersion", "-v" }, "needs a value")]
        [TestCase(new[] { "-pingcoreVersion", ".bad" }, "starting with a letter or digit")]
        [TestCase(new[] { "-pingcoreVersion", "a/b" }, "1 to 64")]
        [TestCase(new[] { "-pingcoreVersion", "a", "-pingcoreVersion", "b" }, "twice")]
        [TestCase(new[] { "-pingcoreVersion", "a", "-pingcoreBackend", "dotnet" }, "IL2CPP or Mono")]
        [TestCase(new[] { "-pingcoreVersion", "a", "-pingcoreScene", "x" }, "unknown flag")]
        [TestCase(new[] { "-pingcoreVersion", "a", "-pingcoreDefine", "X" }, "unknown flag")]
        public void BadArgumentsAreErrors(string[] argv, string expected)
        {
            ClientBuildArgs args = ClientBuildArgs.Parse(argv);
            Assert.That(args.IsValid, Is.False);
            Assert.That(string.Join("; ", args.Errors), Does.Contain(expected));
        }
    }
}
