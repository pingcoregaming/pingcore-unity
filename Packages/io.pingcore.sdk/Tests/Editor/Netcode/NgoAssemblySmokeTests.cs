using System.Linq;
using NUnit.Framework;
using PingCore.Netcode.NGO;
using Unity.Netcode;

namespace PingCore.Netcode.NGO.Tests.Editor
{
    /// <summary>
    /// The NGO assembly compiles only when <c>com.unity.netcode.gameobjects</c> 2.x defines
    /// <c>PINGCORE_NGO</c>. This test exists in the same condition, so it running at all proves
    /// the version define resolved; it then checks the assembly is the one expected, that
    /// NGO itself is loadable beside it, and the approval's constructor shape.
    /// </summary>
    public sealed class NgoAssemblySmokeTests
    {
        [Test]
        public void TheNgoAssemblyCompilesBesideNetcodeForGameObjects()
        {
#if !PINGCORE_NGO
            Assert.Fail("PINGCORE_NGO is not defined, yet this assembly compiled.");
#endif
            Assert.That(typeof(PingCoreConnectionApproval).Assembly.GetName().Name, Is.EqualTo("PingCore.Netcode.NGO"));
            Assert.That(typeof(NetworkManager).Assembly.GetName().Name, Is.EqualTo("Unity.Netcode.Runtime"));
            Assert.That(typeof(PingCoreConnectionApproval).IsSealed, Is.True);
            Assert.That(typeof(PingCoreConnectionApproval).GetConstructors().Single().GetParameters().Select(p => p.ParameterType),
                Is.EqualTo(new[] { typeof(NetworkManager), typeof(PingCore.Core.Handshake.ApprovalOptions), typeof(PingCore.Core.Handshake.IAdmissionEvidence), typeof(PingCore.Core.Handshake.IAdmissionGate) }),
                "the approval is built from a NetworkManager, its options, the evidence and the game gate");
            Assert.That(typeof(System.IDisposable).IsAssignableFrom(typeof(PingCoreConnectionApproval)), Is.True);
        }
    }
}
