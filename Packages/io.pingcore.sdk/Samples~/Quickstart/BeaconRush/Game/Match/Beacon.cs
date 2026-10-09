using Unity.Netcode;
using UnityEngine;

namespace BeaconRush.Match
{
    /// <summary>
    /// A beacon on the field (<c>Prefabs/Beacon.prefab</c>): a network object the game server spawns at a random
    /// point and despawns when a player picks it up. Its position travels in the spawn message; it never moves. How it
    /// looks (the bob, the glow, the pickup flash) is <see cref="BeaconLook"/>, a plain component beside it.
    /// </summary>
    public sealed class Beacon : NetworkBehaviour
    {
        /// <summary>Its position on the arena plane (x, z).</summary>
        public Vector2 Point
        {
            get
            {
                Vector3 position = transform.position;
                return new Vector2(position.x, position.z);
            }
        }

        public override void OnNetworkDespawn()
        {
            BeaconLook look = GetComponent<BeaconLook>();
            if (look != null)
            {
                look.OnDespawned(NetworkManager != null && NetworkManager.IsListening && !NetworkManager.ShutdownInProgress);
            }
        }
    }
}
