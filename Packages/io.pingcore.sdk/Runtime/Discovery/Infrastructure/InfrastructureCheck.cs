using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Unity;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// Says which part of the PingCore backend is missing, from what a game client can see: the public app id in its
    /// settings and Discovery's answers. Run it at launch, before any join, and again when a join fails, and show
    /// <see cref="InfrastructureReport.ToString"/> where the player is looking. One call: the app's public server list
    /// with a page of one (no player token, cacheable, rate limited per address).
    /// <list type="table">
    /// <item><term><see cref="InfrastructureState.NoAppId"/></term><description>the settings hold no app id</description></item>
    /// <item><term><see cref="InfrastructureState.AppUnknown"/></term><description>the list answered 404 with no <c>reason</c>
    /// (Discovery's <c>UnknownApp</c> answer: an unknown public id and a disabled app answer the same, by design), or the id is
    /// not a <c>dscp_</c> public id</description></item>
    /// <item><term><see cref="InfrastructureState.NoGameServers"/></term><description>the list answered with <c>totalServers</c> 0</description></item>
    /// <item><term><see cref="InfrastructureState.Unreachable"/></term><description>no answer at all (a transport error)</description></item>
    /// <item><term><see cref="InfrastructureState.DiscoveryError"/></term><description>anything else, never read as "no game servers"</description></item>
    /// </list>
    /// The classification reads the outcome, the status and the machine-readable <c>reason</c>, never the message text.
    /// In the Editor, the Editor plugin answers the Editor-only <c>InfrastructureEditorHook</c> with the exact reason
    /// from the workspace; a player build has no hook and no workspace key. The list call goes through the client's
    /// retry governor, so an unreachable or degraded Discovery is reported after the client's own retries.
    /// </summary>
    public static class InfrastructureCheck
    {
        /// <summary><see cref="InfrastructureState.NoAppId"/>'s message.</summary>
        public const string NoAppIdMessage = "Not connected to PingCore yet. Set up the game and a fleet in your PingCore panel, then open Window > PingCore, sign in and pick the fleet.";

        /// <summary><see cref="InfrastructureState.AppUnknown"/>'s message.</summary>
        public const string AppUnknownMessage = "PingCore does not recognise this game's app id. Pick the fleet again in Window > PingCore.";

        /// <summary><see cref="InfrastructureState.NoGameServers"/>'s message.</summary>
        public const string NoGameServersMessage = "No game servers are running for this game. Add a deployment to your fleet in the panel.";

        /// <summary><see cref="InfrastructureState.Unreachable"/>'s message.</summary>
        public const string UnreachableMessage = "Cannot reach PingCore Discovery. Check your connection.";

        /// <summary>The start of <see cref="InfrastructureState.DiscoveryError"/>'s message; the outcome and status follow.</summary>
        public const string DiscoveryErrorMessage = "PingCore Discovery could not answer the check";

        /// <summary>How long the check waits for the Editor plugin's detail before it shows the message without it.</summary>
        public static readonly TimeSpan EditorDetailTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The state a server list answer means, pure. <paramref name="appPublicId"/> blank is <see cref="InfrastructureState.NoAppId"/>
        /// and not a <c>dscp_</c> public id <see cref="InfrastructureState.AppUnknown"/>, whatever the answer; otherwise
        /// <paramref name="listAnswer"/> (the answer of <c>GET /v1/apps/{publicId}/servers</c>) decides, with
        /// <paramref name="totalServers"/> its <c>totalServers</c> on success.
        /// </summary>
        public static InfrastructureState Classify(string appPublicId, DiscoveryCallResult listAnswer, int totalServers)
        {
            InfrastructureState? local = LocalState(appPublicId);
            if (local.HasValue)
            {
                return local.Value;
            }

            if (listAnswer == null)
            {
                return InfrastructureState.DiscoveryError;
            }

            switch (listAnswer.Outcome)
            {
                case DiscoveryOutcome.Ok:
                    return totalServers > 0 ? InfrastructureState.Ok : InfrastructureState.NoGameServers;
                case DiscoveryOutcome.NotFound:
                    // The spec's only 404 for this route is UnknownApp, a bare {error, message}: an unknown public id and a
                    // disabled app answer alike. A 404 with a reason is something this SDK was not told about.
                    return listAnswer.Status == 404 && listAnswer.ReasonWire == null ? InfrastructureState.AppUnknown : InfrastructureState.DiscoveryError;
                case DiscoveryOutcome.Unreachable:
                    return InfrastructureState.Unreachable;
                default:
                    return InfrastructureState.DiscoveryError;
            }
        }

        /// <summary>
        /// The state of an app id alone, pure: <see cref="InfrastructureState.NoAppId"/> when blank,
        /// <see cref="InfrastructureState.AppUnknown"/> when it is not a <c>dscp_</c> public id, else null (Discovery decides).
        /// </summary>
        public static InfrastructureState? LocalState(string appPublicId)
        {
            if (string.IsNullOrWhiteSpace(appPublicId))
            {
                return InfrastructureState.NoAppId;
            }

            return DiscoveryClient.IsAppPublicId(appPublicId) ? (InfrastructureState?)null : InfrastructureState.AppUnknown;
        }

        /// <summary>The message for <paramref name="state"/>, or null for <see cref="InfrastructureState.Ok"/>. Pure.</summary>
        /// <param name="state">The state.</param>
        /// <param name="answer">For <see cref="InfrastructureState.DiscoveryError"/>, the answer whose outcome and status the message names; may be null.</param>
        public static string MessageFor(InfrastructureState state, DiscoveryCallResult answer = null)
        {
            switch (state)
            {
                case InfrastructureState.Ok:
                    return null;
                case InfrastructureState.NoAppId:
                    return NoAppIdMessage;
                case InfrastructureState.AppUnknown:
                    return AppUnknownMessage;
                case InfrastructureState.NoGameServers:
                    return NoGameServersMessage;
                case InfrastructureState.Unreachable:
                    return UnreachableMessage;
                default:
                    string what = answer == null ? "no answer" : answer.Status > 0 ? answer.Outcome + " " + answer.Status : answer.Outcome.ToString();
                    return DiscoveryErrorMessage + " (" + what + "). Try again in a moment.";
            }
        }

        /// <summary>
        /// Runs the check. <paramref name="client"/> is the app's Discovery client; it may be null only when
        /// <paramref name="appPublicId"/> is blank or not a <c>dscp_</c> id (no call is made then). In the Editor, the
        /// states <see cref="InfrastructureState.NoAppId"/>, <see cref="InfrastructureState.AppUnknown"/> and
        /// <see cref="InfrastructureState.NoGameServers"/> ask the Editor plugin for the exact reason
        /// (the Editor-only <c>InfrastructureEditorHook</c>), waiting at most <see cref="EditorDetailTimeout"/> on
        /// <paramref name="scheduler"/> (null: the client's, else an <c>AwaitableScheduler</c>, which counts scaled time,
        /// so a paused game waits for the answer). Never throws for a Discovery or Editor failure; a cancelled check reports
        /// <see cref="InfrastructureState.DiscoveryError"/>.
        /// </summary>
        public static async Task<InfrastructureReport> RunAsync(string appPublicId, DiscoveryClient client, IScheduler scheduler, CancellationToken cancellationToken)
        {
            InfrastructureReport report;
            InfrastructureState? local = LocalState(appPublicId);
            if (local.HasValue)
            {
                report = new InfrastructureReport(local.Value, null, 0, null);
            }
            else
            {
                if (client == null)
                {
                    throw new ArgumentNullException(nameof(client), "a Discovery client for the app is needed when the app id is set");
                }

                if (!string.Equals(client.AppPublicId, appPublicId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("the Discovery client is for another app than the one checked", nameof(client));
                }

                DiscoveryResult<ServerPage> answer = await client.ListServersAsync(new ServerListQuery().Page(1), cancellationToken);
                int total = answer.IsOk && answer.Value != null ? answer.Value.TotalServers : 0;
                report = new InfrastructureReport(Classify(appPublicId, answer, total), answer, total, null);
            }

            if (!AsksEditor(report.State))
            {
                return report;
            }

            string detail = await AskEditorAsync(report, appPublicId, scheduler ?? client?.Scheduler, cancellationToken);
            return detail == null ? report : report.WithEditorDetail(detail);
        }

        /// <summary>The states the Editor plugin can explain from the workspace. Pure.</summary>
        internal static bool AsksEditor(InfrastructureState state)
        {
            return state == InfrastructureState.NoAppId || state == InfrastructureState.AppUnknown || state == InfrastructureState.NoGameServers;
        }

#if UNITY_EDITOR
        private static async Task<string> AskEditorAsync(InfrastructureReport report, string appPublicId, IScheduler scheduler, CancellationToken cancellationToken)
        {
            if (!InfrastructureEditorHook.IsRegistered || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var question = new InfrastructureQuestion(report.State, string.IsNullOrWhiteSpace(appPublicId) ? null : appPublicId.Trim(), report.Message);
            return await InfrastructureEditorHook.AskAsync(question, scheduler ?? new AwaitableScheduler(), EditorDetailTimeout, cancellationToken);
        }
#else
        private static Task<string> AskEditorAsync(InfrastructureReport report, string appPublicId, IScheduler scheduler, CancellationToken cancellationToken)
        {
            // A player build has no Editor plugin and no workspace key: never any detail.
            return Task.FromResult<string>(null);
        }
#endif
    }
}
