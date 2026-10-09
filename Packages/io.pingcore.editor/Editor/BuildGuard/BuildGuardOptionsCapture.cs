using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// Captures the <see cref="BuildPlayerOptions"/> of the build that is starting, because
    /// <c>extraScriptingDefines</c> (how a script or a build profile adds an instrumentation define, <see cref="BuildGuardRules"/>)
    /// and the scene list are not visible on the <see cref="BuildReport"/>.
    /// </summary>
    public sealed class BuildGuardOptionsCapture : BuildPlayerProcessor
    {
        public override int callbackOrder => int.MinValue + 100;

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            BuildGuardContext.Capture(buildPlayerContext.BuildPlayerOptions);
        }
    }
}
