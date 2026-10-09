using System;
using System.Globalization;

namespace PingCore.Editor.Workspace.UI.Common
{
    /// <summary>
    /// The workspace panel pages the window links to, pure. The panel's address is the workspace's own
    /// (<c>https://&lt;slug&gt;.app.pingcore.io</c>, or a custom domain), which <c>GET me/capabilities</c> answers as
    /// <c>identity.brandUrl</c> (the brand's primary domain); sign-in and Verify read it, and
    /// <see cref="BaseFrom"/> accepts only a plain <c>https://host</c>. Without it (not read yet in this Editor
    /// session, or the workspace has no domain) the links use <see cref="DefaultBase"/>, which signs the brand
    /// member in to their workspace. The paths are the panel's own routes: <c>/fleets</c>, <c>/fleets/{id}</c>,
    /// <c>/mygames/deployments/deploy?fleetId=N</c> (the existing deploy page, preset to the fleet),
    /// <c>/mygames/deployments/{id}</c>, <c>/mygames/{gameId}/branches</c> and
    /// <c>/mygames/{gameId}/branches/{branchId}/edit</c> (the branch's data source and CDN source).
    /// </summary>
    public static class PanelLinks
    {
        /// <summary>The panel address used when the workspace's own is not known.</summary>
        public const string DefaultBase = "https://app.pingcore.io";

        /// <summary>
        /// <paramref name="brandUrl"/> as a panel base (<c>https://host</c> or <c>https://host:port</c>, no path, query,
        /// fragment or user info; a trailing slash is dropped), or <see cref="DefaultBase"/> for anything else.
        /// </summary>
        public static string BaseFrom(string brandUrl)
        {
            if (string.IsNullOrWhiteSpace(brandUrl) || !Uri.TryCreate(brandUrl.Trim(), UriKind.Absolute, out Uri uri))
            {
                return DefaultBase;
            }

            bool plain = uri.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(uri.UserInfo)
                && (uri.AbsolutePath == "/" || uri.AbsolutePath.Length == 0)
                && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment)
                && Credentials.CredentialTargets.IsValidHost(uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
            if (!plain)
            {
                return DefaultBase;
            }

            return "https://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The Fleets page, where a fleet (and its Discovery app) is created.</summary>
        public static string Fleets(string panelBase) => Root(panelBase) + "/fleets";

        /// <summary>One fleet's page.</summary>
        public static string Fleet(string panelBase, long fleetId) => Root(panelBase) + "/fleets/" + Id(fleetId);

        /// <summary>The deploy page, preset to the fleet: adding a deployment is the billable step, taken in the panel.</summary>
        public static string Deploy(string panelBase, long fleetId) => Root(panelBase) + "/mygames/deployments/deploy?fleetId=" + Id(fleetId);

        /// <summary>One deployment's page.</summary>
        public static string Deployment(string panelBase, long deploymentId) => Root(panelBase) + "/mygames/deployments/" + Id(deploymentId);

        /// <summary>A game's branches page, where a branch is added.</summary>
        public static string GameBranches(string panelBase, long gameId) => Root(panelBase) + "/mygames/" + Id(gameId) + "/branches";

        /// <summary>One branch's edit page, where its data source and CDN source are set.</summary>
        public static string Branch(string panelBase, long gameId, long branchId) => Root(panelBase) + "/mygames/" + Id(gameId) + "/branches/" + Id(branchId) + "/edit";

        private static string Root(string panelBase) => string.IsNullOrEmpty(panelBase) ? DefaultBase : panelBase.TrimEnd('/');

        private static string Id(long id) => id > 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(id), "Ids are positive.");
    }
}
