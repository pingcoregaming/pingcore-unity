using System;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>A file the guard had to scan could not be read; the build fails closed.</summary>
    public sealed class BuildGuardReadException : Exception
    {
        public BuildGuardReadException(string message)
            : base(message)
        {
        }
    }
}
