using BeaconRush.Match;
using Unity.Netcode;
using UnityEngine;

namespace BeaconRush.Networking
{
    /// <summary>
    /// The networked player NGO spawns for every approved connection (<c>Prefabs/Player.prefab</c>, with a
    /// server-authority <c>NetworkTransform</c>). The game server is the only authority over where it is: the
    /// owning client sends its input with <see cref="SubmitInput"/> (at most 20 times a second, plus a keep-alive
    /// every 0.25 s), and the game server moves it with <see cref="MatchRules.Step"/> while a match runs.
    /// A client finds its own player as <see cref="Local"/>. How it looks (its colour, the marker under this client's own
    /// player) is <see cref="PlayerLook"/>, a plain component beside it.
    /// </summary>
    public sealed class BeaconRushPlayer : NetworkBehaviour
    {
        private const float SendInterval = 0.05f;
        private const float KeepAliveInterval = 0.25f;

        private Vector2 serverInput;
        private float serverInputAt = float.NegativeInfinity;
        private Vector2 lastSent;
        private float lastSentAt = float.NegativeInfinity;
        private Vector2 pending;
        private bool hasPending;

        /// <summary>This client's own player, or null before it spawned (and always null on a dedicated game server).</summary>
        public static BeaconRushPlayer Local { get; private set; }

        /// <summary>Its position on the arena plane (x, z).</summary>
        public Vector2 Point
        {
            get
            {
                Vector3 position = transform.position;
                return new Vector2(position.x, position.z);
            }
        }

        /// <summary>
        /// The owning client's movement input, a direction of length at most 1 on the arena plane (x right, y up the
        /// screen = z). Call it every frame from the client's input code; sends are throttled here.
        /// </summary>
        public void SubmitInput(Vector2 move)
        {
            if (!IsOwner || !IsSpawned)
            {
                return;
            }

            pending = MatchRules.ClampInput(move);
            hasPending = true;
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner && IsClient)
            {
                Local = this;
            }

            if (IsServer)
            {
                Vector2 spawn = MatchRules.SpawnPoint(OwnerClientId);
                transform.position = new Vector3(spawn.x, 0f, spawn.y);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (Local == this)
            {
                Local = null;
            }
        }

        private void Update()
        {
            if (!IsSpawned || !IsOwner || !IsClient || !hasPending)
            {
                return;
            }

            float now = Time.unscaledTime;
            bool changed = (pending - lastSent).sqrMagnitude > 1e-4f;
            if ((changed && now - lastSentAt >= SendInterval) || now - lastSentAt >= KeepAliveInterval)
            {
                lastSent = pending;
                lastSentAt = now;
                SubmitInputRpc(pending);
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner, Delivery = RpcDelivery.Unreliable)]
        private void SubmitInputRpc(Vector2 move)
        {
            serverInput = MatchRules.ClampInput(move);
            serverInputAt = Time.unscaledTime;
        }

        // ---- game server only ---------------------------------------------------------------------

        /// <summary>The owning client's last input, or zero when it is older than <see cref="MatchRules.InputTimeoutSeconds"/>.</summary>
        internal Vector2 ServerInput => Time.unscaledTime - serverInputAt <= MatchRules.InputTimeoutSeconds ? serverInput : Vector2.zero;

        /// <summary>Moves the player one step with <paramref name="input"/> (its own, or a bot's).</summary>
        internal void ServerStep(Vector2 input, float seconds)
        {
            Vector2 next = MatchRules.Step(Point, input, seconds);
            transform.position = new Vector3(next.x, 0f, next.y);
        }

        /// <summary>Back to its spawn point (a new match).</summary>
        internal void ServerRespawn()
        {
            Vector2 spawn = MatchRules.SpawnPoint(OwnerClientId);
            transform.position = new Vector3(spawn.x, 0f, spawn.y);
            serverInputAt = float.NegativeInfinity;
        }
    }
}
