#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// What <see cref="InfrastructureCheck"/> asks the Editor plugin: the state the game client found, the app id it
    /// checked (a public <c>dscp_</c> id, or null) and the message it will show. Editor only.
    /// </summary>
    public sealed class InfrastructureQuestion
    {
        /// <summary>Creates a question.</summary>
        public InfrastructureQuestion(InfrastructureState state, string appPublicId, string message)
        {
            State = state;
            AppPublicId = appPublicId;
            Message = message;
        }

        /// <summary>What the game client found.</summary>
        public InfrastructureState State { get; }

        /// <summary>The app id the client checked, or null when its settings hold none.</summary>
        public string AppPublicId { get; }

        /// <summary>The message the client shows (<see cref="InfrastructureCheck.MessageFor"/>).</summary>
        public string Message { get; }
    }

    /// <summary>
    /// The Editor-only extension point behind <see cref="InfrastructureReport.EditorDetail"/>. This whole file compiles
    /// only with <c>UNITY_EDITOR</c>, so a player build carries neither the type nor a way to reach the workspace. The
    /// Editor plugin (<c>io.pingcore.editor</c>, <c>Editor/Workspace/Diagnostics/</c>) registers one answerer when the
    /// Editor loads; it reads the picked fleet from the workspace with the signed-in key, read-only, and answers one
    /// sentence such as "Fleet Beacon Rush has no deployment.". The check waits for it at most
    /// <see cref="InfrastructureCheck.EditorDetailTimeout"/> on the game's scheduler, never blocks Play, and drops an
    /// answer that throws, comes late, is blank, or holds anything shaped like a credential.
    /// <para>
    /// <b>The answerer runs on a thread-pool thread,</b> never on the game's main thread: the timeout starts first, then
    /// the answerer is started with <c>Task.Run</c>, so an answerer that blocks before it returns its task (a slow file,
    /// a credential store, a stuck call) can delay neither Play nor the check past the timeout. It must therefore not
    /// touch a main-thread-only Unity API directly; the Editor plugin takes its project folder when it registers and
    /// marshals its <c>EditorPrefs</c> and <c>SessionState</c> reads to the main thread, each bounded. The result still
    /// reaches the caller on its own context (the main thread). This is the Discovery client's one <c>Task.Run</c>, an
    /// exception to its no-<c>Task.Run</c> rule (<c>DiscoveryAsyncBanTests</c>) that exists only in the Editor.
    /// </para>
    /// </summary>
    public static class InfrastructureEditorHook
    {
        /// <summary>The longest detail kept; a longer answer is cut there.</summary>
        public const int MaxDetailLength = 400;

        private static Answerer current;

        /// <summary>
        /// Answers one question with a sentence, or null when there is nothing to add. Called on a thread-pool thread (see
        /// the class remarks), so it touches no main-thread-only Unity API. Must not throw; a throw counts as null.
        /// </summary>
        public delegate Task<string> Answerer(InfrastructureQuestion question, CancellationToken cancellationToken);

        /// <summary>The registered answerer, or null (tests save and restore it).</summary>
        internal static Answerer Current => Volatile.Read(ref current);

        /// <summary>True while an answerer is registered.</summary>
        public static bool IsRegistered => Volatile.Read(ref current) != null;

        /// <summary>Registers the one answerer (a later call replaces it); null removes it.</summary>
        public static void Register(Answerer answerer) => Volatile.Write(ref current, answerer);

        /// <summary>
        /// Asks the registered answerer, racing it against <paramref name="timeout"/> on <paramref name="scheduler"/>.
        /// The timeout starts before any answerer work, and the answerer runs on a thread-pool thread, so not even an
        /// answerer that blocks synchronously holds the caller's thread or the check past the timeout.
        /// Null when none is registered, it threw, it ran out of time (its token is then cancelled), the caller cancelled,
        /// the scheduler threw, or its answer was blank or credential-shaped (<see cref="Clean"/>). Never throws.
        /// </summary>
        internal static async Task<string> AskAsync(InfrastructureQuestion question, IScheduler scheduler, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Answerer answerer = Volatile.Read(ref current);
            if (answerer == null || question == null || scheduler == null)
            {
                return null;
            }

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task delay;
                try
                {
                    delay = scheduler.DelayAsync(timeout, linked.Token);
                }
                catch (Exception)
                {
                    // A scheduler that throws is a timeout, never an exception out of the check; the answerer is not asked.
                    return null;
                }

                // Off the caller's thread: a throw becomes a faulted task, a null task an answer of null. The token is
                // taken here, so a pool thread that starts late never reads a disposed source.
                CancellationToken token = linked.Token;
                Task<string> asked = Task.Run(() => answerer(question, token) ?? Task.FromResult<string>(null));
                Task first;
                try
                {
                    first = await Task.WhenAny(asked, delay);
                }
                catch (Exception)
                {
                    first = null;
                }
                finally
                {
                    // Stops the answerer's reads and the timer, whichever finished first.
                    linked.Cancel();
                    Observe(asked);
                    Observe(delay);
                }

                if (first != asked || asked.Status != TaskStatus.RanToCompletion || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                return Clean(await asked);
            }
        }

        /// <summary>
        /// The detail as shown: trimmed, cut to <see cref="MaxDetailLength"/>, null when blank or when it holds anything
        /// shaped like a credential (<see cref="DiscoveryClient.LooksLikeCredential"/>), so the banner never shows a
        /// token whatever the answerer returned. Pure.
        /// </summary>
        internal static string Clean(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail) || DiscoveryClient.LooksLikeCredential(detail))
            {
                return null;
            }

            string trimmed = detail.Trim();
            return trimmed.Length <= MaxDetailLength ? trimmed : trimmed.Substring(0, MaxDetailLength);
        }

        private static void Observe(Task task)
        {
            // A late answerer or a cancelled delay may fault after the race; never leave that unobserved.
            task?.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
#endif
