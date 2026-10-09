using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Connect;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>Whether Release may run, why not, and what it should warn about. Immutable.</summary>
    public sealed class ReleaseGateResult
    {
        public ReleaseGateResult(bool allowed, string refusal, string warning, bool offerDeploy)
        {
            Allowed = allowed;
            Refusal = refusal;
            Warning = warning;
            OfferDeploy = offerDeploy;
        }

        /// <summary>Release may run.</summary>
        public bool Allowed { get; }

        /// <summary>Why Release cannot run, or null.</summary>
        public string Refusal { get; }

        /// <summary>A warning shown at Release that does not stop it, or null.</summary>
        public string Warning { get; }

        /// <summary>Show the panel's deploy page link (the fleet has no deployment).</summary>
        public bool OfferDeploy { get; }
    }

    /// <summary>
    /// Release is the only Ship step that needs deployments: a release moves the fleet's
    /// deployments onto a snapshot, so a fleet with none has nothing to release onto, while Build and Push never
    /// depend on them. With deployments, the fleet's build targets name the CDN source they all deliver from; a null
    /// one (members that disagree, or a workspace older than the field) refuses Release as it always did, and one that
    /// differs from the source the branch pushes to is a warning, because the release would wait for a snapshot that
    /// source never gets. Pure.
    /// </summary>
    public static class ReleaseGate
    {
        /// <summary>Release with no deployment.</summary>
        public const string NoDeploymentMessage = "Pushed builds wait in the CDN source. Add a deployment to the fleet in the panel to run one.";

        /// <summary>The fleet's members deliver from a different CDN source than the one pushed to.</summary>
        public static string OtherSourceWarning(long fleetSourceId, long pushedSourceId) =>
            $"This fleet's deployments deliver from CDN source #{fleetSourceId}, but you pushed to #{pushedSourceId}. The release will not find your snapshot until they deliver from the same source; check the branch you push to, or the deployments' branch in the panel.";

        /// <summary>
        /// The gate for <paramref name="facts"/> (the fleet read most recently; null before Connect read it) and
        /// <paramref name="pushedSourceId"/>, the CDN source Push pushes to (0 when unknown).
        /// </summary>
        public static ReleaseGateResult Evaluate(FleetFacts facts, long pushedSourceId)
        {
            if (facts == null)
            {
                return new ReleaseGateResult(false, "The fleet is not read yet. Press Refresh under Connect.", null, false);
            }

            if (facts.DeploymentCount == 0)
            {
                return new ReleaseGateResult(false, NoDeploymentMessage, null, true);
            }

            if (facts.ReleaseProblem != null)
            {
                return new ReleaseGateResult(false, facts.ReleaseProblem, null, false);
            }

            string warning = pushedSourceId > 0 && facts.CdnSourceId > 0 && facts.CdnSourceId != pushedSourceId
                ? OtherSourceWarning(facts.CdnSourceId, pushedSourceId)
                : null;
            return new ReleaseGateResult(true, null, warning, false);
        }
    }
}
