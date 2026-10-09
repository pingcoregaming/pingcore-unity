using PingCore.Core.Discovery;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The result of <see cref="InfrastructureCheck.RunAsync"/>: the state, its message for the player (or developer)
    /// and, in the Editor only, the exact reason the Editor plugin read from the workspace. Carries no token.
    /// </summary>
    public sealed class InfrastructureReport
    {
        /// <summary>Creates a report.</summary>
        /// <param name="state">The state.</param>
        /// <param name="answer">The server list answer the state was read from, or null when no call was made.</param>
        /// <param name="totalServers">The listed game servers (0 when the list did not answer).</param>
        /// <param name="editorDetail">The Editor plugin's exact reason, or null.</param>
        public InfrastructureReport(InfrastructureState state, DiscoveryCallResult answer, int totalServers, string editorDetail)
        {
            State = state;
            Answer = answer;
            TotalServers = totalServers;
            Message = InfrastructureCheck.MessageFor(state, answer);
            EditorDetail = string.IsNullOrWhiteSpace(editorDetail) ? null : editorDetail;
        }

        /// <summary>The state.</summary>
        public InfrastructureState State { get; }

        /// <summary>True for <see cref="InfrastructureState.Ok"/>.</summary>
        public bool IsOk => State == InfrastructureState.Ok;

        /// <summary>The state's message, or null when <see cref="IsOk"/>.</summary>
        public string Message { get; }

        /// <summary>
        /// In the Editor only: the exact reason the Editor plugin read from the workspace with the signed-in key
        /// (for example "Fleet Beacon Rush has no deployment."), or null. Always null in a player build.
        /// </summary>
        public string EditorDetail { get; }

        /// <summary>The server list answer behind the state, or null when no call was made.</summary>
        public DiscoveryCallResult Answer { get; }

        /// <summary>The game servers the list counted (<c>totalServers</c>), 0 when it did not answer.</summary>
        public int TotalServers { get; }

        /// <summary>A copy with <paramref name="detail"/> as its <see cref="EditorDetail"/>.</summary>
        internal InfrastructureReport WithEditorDetail(string detail) => new InfrastructureReport(State, Answer, TotalServers, detail);

        /// <summary>The message, then the Editor detail on a line of its own when there is one; empty when <see cref="IsOk"/>.</summary>
        public override string ToString()
        {
            if (IsOk)
            {
                return string.Empty;
            }

            return EditorDetail == null ? Message : Message + "\n" + EditorDetail;
        }
    }
}
