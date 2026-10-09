namespace PingCore.Editor.BuildGuard
{
    /// <summary>Why the build guard rejected a build. The string codes are in <see cref="BuildGuardReasons"/>.</summary>
    public enum BuildGuardReason
    {
        /// <summary>A <c>PingCore.Editor*</c> assembly is part of the player build.</summary>
        EditorAssembly,

        /// <summary>
        /// A restricted assembly or an instrumentation define (the project's <see cref="BuildGuardRules"/>) in a build that
        /// does not write inside the rules' instrumented output folder.
        /// </summary>
        InstrumentationInPlainBuild,

        /// <summary>
        /// A token-shaped <c>usr_</c>, <c>sys_</c>, <c>cdnpush_</c> or <c>dsc_</c> string, or a configured
        /// <c>openRegistrationHeartbeatToken</c> whose scope is not confirmed.
        /// </summary>
        SecretLiteral,

        /// <summary>
        /// The output cannot be byte-scanned (no shipped assembly list, or a compressed archive) and
        /// the source-side scan of what the build packed did not run, so nothing vouches for the build.
        /// </summary>
        OutputUnscannable,

        /// <summary>
        /// The guard could not finish a scan (a file it could not read, a folder it could not list, a
        /// malformed shipped assembly list), so it cannot vouch for the build and fails closed.
        /// </summary>
        ScanError,
    }

    /// <summary>The stable string codes written to the verdict file and the build log.</summary>
    public static class BuildGuardReasons
    {
        public const string EditorAssembly = "editor_assembly";
        public const string InstrumentationInPlainBuild = "instrumentation_in_plain_build";
        public const string SecretLiteral = "secret_literal";
        public const string OutputUnscannable = "output_unscannable";
        public const string ScanError = "scan_error";

        public static string ToCode(BuildGuardReason reason)
        {
            switch (reason)
            {
                case BuildGuardReason.EditorAssembly:
                    return EditorAssembly;
                case BuildGuardReason.InstrumentationInPlainBuild:
                    return InstrumentationInPlainBuild;
                case BuildGuardReason.SecretLiteral:
                    return SecretLiteral;
                case BuildGuardReason.OutputUnscannable:
                    return OutputUnscannable;
                case BuildGuardReason.ScanError:
                    return ScanError;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(reason), reason, "Unknown build guard reason.");
            }
        }
    }
}
