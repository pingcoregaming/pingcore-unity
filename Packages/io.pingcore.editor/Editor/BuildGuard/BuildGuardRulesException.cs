using System;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// <c>ProjectSettings/PingCoreBuildGuardRules.json</c> could not be read as rules; the build fails closed. The message
    /// names the file and every problem in plain words, and quotes nothing from the file but a key name.
    /// </summary>
    public sealed class BuildGuardRulesException : FormatException
    {
        public BuildGuardRulesException(string message)
            : base(message)
        {
        }
    }
}
