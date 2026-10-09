using UnityEngine;
#if PINGCORE_DISCOVERY_URL_OVERRIDE
using UnityEngine.Serialization;
#endif

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// Client configuration asset: the public <c>dscp_</c> ids of the two Discovery apps, and the
    /// one secret a player build may carry, the open-registration app's heartbeat token for listen
    /// hosting. Nothing a private app owns belongs here. The Discovery base URL is not a setting:
    /// it is <see cref="DefaultDiscoveryBaseUrl"/>, which no studio ever changes. Only a project
    /// compiled with the <c>PINGCORE_DISCOVERY_URL_OVERRIDE</c> scripting define gets a serialized
    /// override, which takes over the value an older asset kept under <c>discoveryBaseUrl</c>; the
    /// define is for testing against another Discovery host, and nothing in this repository sets
    /// it. The Editor plugin (Window &gt; PingCore) owns the asset: Connect writes
    /// the fleet app's public id when the developer picks the fleet, and the Player hosting fold the
    /// community app's id and its confirmed heartbeat token. It finds the project's one asset or
    /// creates it under a <c>Resources/</c> folder (<see cref="ResourcesName"/>). A repository that is
    /// shared or published keeps both ids empty: each developer's Connect fills them locally, and
    /// <see cref="InfrastructureCheck"/> tells a game launched without them what is missing. The build
    /// guard in <c>io.pingcore.editor</c> reads
    /// <c>openRegistrationHeartbeatToken</c> by name; renaming that field breaks the guard.
    /// </summary>
    [CreateAssetMenu(fileName = "PingCoreClientSettings", menuName = "PingCore/Client Settings")]
    public sealed class PingCoreClientSettings : ScriptableObject
    {
        /// <summary>The Discovery base URL every player build calls, without a trailing slash.</summary>
        public const string DefaultDiscoveryBaseUrl = "https://discovery.pingcore.io";

        /// <summary>The name <see cref="LoadFromResources"/> loads, when the asset sits in a <c>Resources/</c> folder.</summary>
        public const string ResourcesName = "PingCoreClientSettings";

#if PINGCORE_DISCOVERY_URL_OVERRIDE
        [Tooltip("PINGCORE_DISCOVERY_URL_OVERRIDE only, for testing against another host: a Discovery base URL instead of https://discovery.pingcore.io. Empty for the default.")]
        [FormerlySerializedAs("discoveryBaseUrl")]
        [SerializeField]
        private string discoveryBaseUrlOverride = string.Empty;
#endif

        [Tooltip("Public id (dscp_...) of the private Discovery app linked to the fleet: matchmaking and the hosted browser tab.")]
        [SerializeField]
        private string fleetAppPublicId = string.Empty;

        [Tooltip("Public id (dscp_...) of the open-registration Discovery app: the community browser tab, self-hosted and listen hosts.")]
        [SerializeField]
        private string communityAppPublicId = string.Empty;

        [Tooltip("Heartbeat token of the OPEN-registration app only, used by listen hosts. Never a private app's token.")]
        [SerializeField]
        private string openRegistrationHeartbeatToken = string.Empty;

        /// <summary>True when this build was compiled with the <c>PINGCORE_DISCOVERY_URL_OVERRIDE</c> define.</summary>
        public static bool DiscoveryUrlOverrideCompiledIn
        {
            get
            {
#if PINGCORE_DISCOVERY_URL_OVERRIDE
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// The Discovery base URL: <see cref="DefaultDiscoveryBaseUrl"/>, unless the build carries the
        /// <c>PINGCORE_DISCOVERY_URL_OVERRIDE</c> define and the asset sets an override.
        /// </summary>
        public string DiscoveryBaseUrl
        {
            get
            {
#if PINGCORE_DISCOVERY_URL_OVERRIDE
                return ResolveDiscoveryBaseUrl(true, discoveryBaseUrlOverride);
#else
                return ResolveDiscoveryBaseUrl(false, null);
#endif
            }
        }

        /// <summary>Public id of the private, fleet-linked Discovery app.</summary>
        public string FleetAppPublicId => fleetAppPublicId;

        /// <summary>Public id of the open-registration community Discovery app.</summary>
        public string CommunityAppPublicId => communityAppPublicId;

        /// <summary>
        /// The open-registration app's heartbeat token. Treat it as a secret: never log it,
        /// never show it in UI.
        /// </summary>
        public string OpenRegistrationHeartbeatToken => openRegistrationHeartbeatToken;

        /// <summary>
        /// The rule behind <see cref="DiscoveryBaseUrl"/>, pure: the override (trimmed, without a
        /// trailing slash) only when it is compiled in and not blank, else <see cref="DefaultDiscoveryBaseUrl"/>.
        /// </summary>
        public static string ResolveDiscoveryBaseUrl(bool overrideCompiledIn, string overrideValue)
        {
            if (!overrideCompiledIn || string.IsNullOrWhiteSpace(overrideValue))
            {
                return DefaultDiscoveryBaseUrl;
            }

            return overrideValue.Trim().TrimEnd('/');
        }

        /// <summary>The asset named <see cref="ResourcesName"/> in a <c>Resources/</c> folder, or null.</summary>
        public static PingCoreClientSettings LoadFromResources() => Resources.Load<PingCoreClientSettings>(ResourcesName);
    }
}
