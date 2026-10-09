using System;

namespace PingCore.Core
{
    /// <summary>
    /// Marks a type or member that managed code stripping must keep. IL2CPP honours any
    /// attribute named <c>PreserveAttribute</c>, so Core declares its own and stays free of
    /// any engine reference. Every wire DTO carries it, alongside the assembly-level
    /// <c>link.xml</c> entries.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public sealed class PreserveAttribute : Attribute
    {
    }
}
