using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Discovery.Client;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using InitializeOnLoad = UnityEditor.InitializeOnLoadAttribute;

namespace PingCore.Editor.Workspace.Infrastructure
{
    /// <summary>
    /// Registers the Editor plugin as the answerer of the runtime's Editor-only extension point
    /// (<see cref="InfrastructureEditorHook"/>) whenever the Editor loads scripts. Registering costs nothing: no file,
    /// key or network is touched until a game in Play mode asks, and then only the fleet id from
    /// <c>ProjectSettings/PingCoreEditor.json</c>, whether a key is stored (asked with <c>Exists</c>) and the two
    /// read-only fleet GETs (<see cref="InfrastructureDetailSource"/>). Not signed in, no fleet picked, or any failure
    /// answers null, never an exception, so Play is never blocked and the game shows its own message.
    /// <para>
    /// The runtime calls the answerer on a thread-pool thread, after its 5 s timeout has started. So the project folder
    /// is taken here, on the main thread, as the Editor loads; the settings files are plain file reads on the pool
    /// thread; the credential store choice and every <c>EditorPrefs</c> or <c>SessionState</c> read go to the main thread
    /// through <see cref="MainThreadCalls"/>, each waited on for at most <see cref="MainThreadBound"/>
    /// (<see cref="MainThreadCredentialStore"/>); Windows Credential Manager and the HTTP calls stay on the pool thread.
    /// </para>
    /// The folder is <c>Diagnostics/</c>; the namespace is <c>Infrastructure</c>, because a <c>Workspace.Diagnostics</c>
    /// namespace would shadow <c>System.Diagnostics</c> for the code that names <c>Diagnostics.Process</c>.
    /// </summary>
    [InitializeOnLoad]
    internal static class InfrastructureDetailHook
    {
        /// <summary>The longest one main-thread read (the store choice, one <c>EditorPrefs</c> read) may wait.</summary>
        internal static readonly TimeSpan MainThreadBound = TimeSpan.FromSeconds(2);

        static InfrastructureDetailHook()
        {
            // On the main thread, as the Editor loads scripts: what the pool thread may not ask Unity for later.
            string projectRoot = WorkspaceContext.ProjectRoot;
            MainThreadCalls mainThread = MainThreadCalls.ForEditor();
            InfrastructureEditorHook.Register((question, cancellationToken) =>
                AnswerAsync(projectRoot, mainThread, WorkspaceContext.Store, WorkspaceContext.Api, question, cancellationToken));
        }

        /// <summary>
        /// The detail for <paramref name="question"/> from the project at <paramref name="projectRoot"/>, or null. Safe on
        /// any thread: <paramref name="storeFor"/> (the store for the key's "this session only" choice) and
        /// <paramref name="apiFor"/> run on the main thread through <paramref name="mainThread"/>, and the store they give
        /// is wrapped so its reads do too. Never throws.
        /// </summary>
        internal static async Task<string> AnswerAsync(
            string projectRoot,
            MainThreadCalls mainThread,
            Func<bool, ICredentialStore> storeFor,
            Func<WorkspaceEndpoint, ICredentialStore, IPingCoreApi> apiFor,
            InfrastructureQuestion question,
            CancellationToken cancellationToken)
        {
            try
            {
                EditorProjectSettings project;
                EditorUserSettings user;
                try
                {
                    project = EditorProjectSettings.Load(projectRoot);
                    user = EditorUserSettings.Load(projectRoot);
                }
                catch (InvalidDataException)
                {
                    return null;
                }

                if (project.FleetId <= 0 || !WorkspaceEndpoint.Resolve(user.ApiBaseOverride, out WorkspaceEndpoint endpoint, out _))
                {
                    return null;
                }

                (ICredentialStore store, IPingCoreApi api) = mainThread.Run(() =>
                {
                    ICredentialStore chosen = MainThreadCredentialStore.Wrap(storeFor(user.SessionOnlyKey), mainThread, MainThreadBound);
                    return (chosen, chosen == null ? null : apiFor(endpoint, chosen));
                }, MainThreadBound);

                if (api == null || cancellationToken.IsCancellationRequested || !WorkspaceContext.HasKey(store, endpoint.Host))
                {
                    return null;
                }

                return await InfrastructureDetailSource.AnswerAsync(api, project.FleetId, question, cancellationToken);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
