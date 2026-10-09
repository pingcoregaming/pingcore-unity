using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeaconRush.Session;
using Newtonsoft.Json.Linq;
using PingCore.Core.Handshake;
using PingCore.Fleet;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// The game server's extension points, for instrumentation code the game does not reference (an automation harness,
    /// a profiler, a debug overlay). Every member's default leaves the game exactly as it plays without one, so the game
    /// runs with <see cref="None"/> unless code outside it sets <see cref="Current"/>, typically from a
    /// <c>[RuntimeInitializeOnLoadMethod]</c> in its own assembly before the first scene loads. The game reads
    /// <see cref="Current"/> where each hook applies; together with the <see cref="ServerEvents"/> stream, this is the
    /// whole surface. Main thread only.
    /// </summary>
    public class ServerInstrumentation
    {
        private static ServerInstrumentation current;

        /// <summary>The instrumentation that changes nothing.</summary>
        public static ServerInstrumentation None { get; } = new ServerInstrumentation();

        /// <summary>The installed instrumentation; <see cref="None"/> when nothing was installed. Setting null restores <see cref="None"/>.</summary>
        public static ServerInstrumentation Current
        {
            get => current ?? None;
            set => current = value;
        }

        /// <summary>
        /// Hosted only: awaited after <c>listening</c> and before <c>ReadyAsync</c>. An exception is logged and the boot goes
        /// on to Ready. The default returns at once.
        /// </summary>
        public virtual Task BeforeReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Hosted only: true holds the first <c>players</c> write back until after a 2xx <c>ReadyAsync</c>, so a call made in
        /// <see cref="BeforeReadyAsync"/> is the only write the local SDK endpoint sees before Ready (<see cref="BootPlan"/>).
        /// The default, false, writes <c>players = 0</c> before listening.
        /// </summary>
        public virtual bool DeferFirstPlayersWrite => false;

        /// <summary>Hosted only: each <c>players</c> counter write returned, with its outcome, retries included.</summary>
        public virtual void PlayersWriteReturned(FleetCallOutcome outcome)
        {
        }

        /// <summary>
        /// Adjusts a hosting mode's approval options before NGO listens and before the <c>hosting</c> event reports them. The
        /// options arrive with the SDK's defaults and the game sets its own fields afterwards (the protocol version, the
        /// hosting mode, LAN only and the session claim), so only the options the game leaves at their defaults can change;
        /// the default changes none. Whatever an instrumentation switches on is its own responsibility: <c>AllowSelfAllocatedJoins</c>, for
        /// one, admits a <c>match</c> join on a supervisor self-allocation's guessable id, so an instrumentation that sets
        /// it must never be installed in a build that reaches players.
        /// </summary>
        public virtual void ConfigureApproval(GameHostingMode mode, ApprovalOptions options)
        {
        }

        /// <summary>
        /// The heartbeat tier's admission evidence (a self-hosted game server, an online listen host) as the approval uses
        /// it. A wrapper may watch what the evidence gathered; it must pass every call through. The default returns
        /// <paramref name="evidence"/> itself.
        /// </summary>
        public virtual IAdmissionEvidence WrapHeartbeatEvidence(IAdmissionEvidence evidence) => evidence;

        /// <summary>Extra fields for the <c>approval</c> event of <paramref name="decision"/>, appended after the game's own; null for none.</summary>
        public virtual IEnumerable<KeyValuePair<string, object>> ApprovalEventFields(AdmissionDecision decision) => null;

        /// <summary>
        /// The settings of an allocated session (hosted): the allocation's context and its roster's ticket contexts in roster
        /// order (null entries for tickets with none). The default is <paramref name="defaults"/>, whatever the contexts say.
        /// </summary>
        public virtual SessionSettings AllocatedSessionSettings(JObject allocationContext, IReadOnlyList<JObject> ticketContexts, SessionSettings defaults) => defaults;

        /// <summary>The settings of a local session (a listen host's), from <paramref name="defaults"/>. The default returns them unchanged.</summary>
        public virtual SessionSettings LocalSessionSettings(GameHostingMode mode, SessionSettings defaults) => defaults;

        /// <summary>
        /// <see cref="Current"/>'s answer, or <paramref name="fallback"/> when the instrumentation threw: a hook never stops
        /// the game. The exception is logged.
        /// </summary>
        internal static T Ask<T>(Func<ServerInstrumentation, T> question, T fallback)
        {
            try
            {
                return question(Current);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                return fallback;
            }
        }

        /// <summary>Tells <see cref="Current"/> something; an exception is logged and dropped.</summary>
        internal static void Tell(Action<ServerInstrumentation> notice)
        {
            try
            {
                notice(Current);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }
    }
}
