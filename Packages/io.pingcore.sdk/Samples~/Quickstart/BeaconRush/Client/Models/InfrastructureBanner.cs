using PingCore.Discovery.Client;

namespace BeaconRush.Client.Models
{
    /// <summary>
    /// What the client shows about its PingCore backend, pure. At launch, before any join, the client runs the SDK's
    /// <see cref="InfrastructureCheck"/> on the fleet app and the menu shows its message (and, in the Editor, the Editor
    /// plugin's exact reason under it) until the check reads Ok. A failed join runs the check again and shows the same
    /// message when it explains the failure, else the join's own words.
    /// </summary>
    public static class InfrastructureBanner
    {
        /// <summary>
        /// The fleet app id to check: the settings' id as it is (an id with stray spaces is not a public id, and the check
        /// says so), or empty for an empty id and for the shipped placeholder (<see cref="ClientApps.PlaceholderPublicId"/>),
        /// which both mean "not connected yet".
        /// </summary>
        public static string AppIdToCheck(string configured) => ClientApps.IsUnconfigured(configured) ? string.Empty : configured;

        /// <summary>The banner's message, or null when there is nothing to say (no report yet, or Ok).</summary>
        public static string Headline(InfrastructureReport report) => report == null || report.IsOk ? null : report.Message;

        /// <summary>The Editor's exact reason under the message, or null (always null in a player build).</summary>
        public static string Detail(InfrastructureReport report) => report == null || report.IsOk ? null : report.EditorDetail;

        /// <summary>
        /// True when the report explains a failed join on its own: the backend is missing a piece, or Discovery cannot be
        /// reached. A Discovery error says nothing a join's own answer does not, and Ok means the backend is there.
        /// </summary>
        public static bool Explains(InfrastructureReport report)
        {
            if (report == null)
            {
                return false;
            }

            switch (report.State)
            {
                case InfrastructureState.NoAppId:
                case InfrastructureState.AppUnknown:
                case InfrastructureState.NoGameServers:
                case InfrastructureState.Unreachable:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when Quick play must not fall back to Find match: no fleet app to queue on, or no Discovery to queue with.
        /// No game server listed does not block: the list hides draining game servers (a release, a scale-up), and a
        /// queued ticket is the demand that starts one.
        /// </summary>
        public static bool BlocksMatchmaking(InfrastructureReport report)
        {
            return report != null
                && (report.State == InfrastructureState.NoAppId || report.State == InfrastructureState.AppUnknown || report.State == InfrastructureState.Unreachable);
        }

        /// <summary>What a failed join says: the check's message (with the Editor's detail) when it explains it, else <paramref name="joinText"/>.</summary>
        public static string JoinFailure(InfrastructureReport report, string joinText) => Explains(report) ? report.ToString() : joinText;
    }
}
