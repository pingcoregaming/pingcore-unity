using System;
using System.Collections.Generic;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// One reason a build is rejected. <see cref="Detail"/> never contains a matched token: it names
    /// the prefix, the length and the position only.
    /// </summary>
    public sealed class BuildGuardFinding
    {
        public BuildGuardFinding(BuildGuardReason reason, string location, string detail)
        {
            Reason = reason;
            Location = location ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public BuildGuardReason Reason { get; }

        public string Code => BuildGuardReasons.ToCode(Reason);

        /// <summary>A file path or an assembly name.</summary>
        public string Location { get; }

        public string Detail { get; }

        public override string ToString() =>
            Code + ": " + Location + (Detail.Length > 0 ? " (" + Detail + ")" : string.Empty);
    }

    /// <summary>The outcome of scanning one file or one string.</summary>
    public sealed class BuildGuardScanResult
    {
        public static readonly BuildGuardScanResult Empty = new BuildGuardScanResult(Array.Empty<BuildGuardFinding>(), 0);

        public BuildGuardScanResult(IReadOnlyList<BuildGuardFinding> findings, int allowedDscTokenHits)
        {
            Findings = findings ?? Array.Empty<BuildGuardFinding>();
            AllowedDscTokenHits = allowedDscTokenHits;
        }

        /// <summary>Every <c>secret_literal</c> hit that is not a configured open-registration heartbeat token.</summary>
        public IReadOnlyList<BuildGuardFinding> Findings { get; }

        /// <summary>How many <c>dsc_</c> hits equalled a configured <c>openRegistrationHeartbeatToken</c>.</summary>
        public int AllowedDscTokenHits { get; }
    }
}
