using System.Collections.Generic;
using BeaconRush.Networking;
using UnityEngine;

namespace BeaconRush.Match
{
    /// <summary>
    /// How a player looks on a client (<c>Prefabs/Player.prefab</c>): the body takes its <see cref="PlayerPalette"/> colour by
    /// join order, and this client's own player shows a ring with an arrow on the ground that turns toward where it last
    /// moved. A plain <c>MonoBehaviour</c>, so the network surface (protocol 2) is untouched; it does nothing on a dedicated
    /// game server or in batchmode.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerLook : MonoBehaviour
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly List<PlayerLook> Live = new List<PlayerLook>();
        private static readonly List<ulong> BoardIds = new List<ulong>();
        private static int boardIdsFrame = -1;

        [SerializeField]
        [Tooltip("The body renderer that takes the player's colour.")]
        private Renderer body;

        [SerializeField]
        [Tooltip("The ring and arrow under this client's own player; hidden for everyone else.")]
        private GameObject marker;

        [SerializeField]
        [Tooltip("The pivot the arrow sits on; turned toward the last movement.")]
        private Transform heading;

        private BeaconRushPlayer player;
        private MaterialPropertyBlock block;
        private int appliedSlot = -1;
        private Vector3 lastPosition;

        /// <summary>The players drawn on this client now.</summary>
        public static IReadOnlyList<PlayerLook> All => Live;

        /// <summary>The colour slot shown, or -1 before the first frame.</summary>
        public int Slot => appliedSlot;

        /// <summary>Its position on the arena plane (x, z).</summary>
        public Vector2 Point => new Vector2(transform.position.x, transform.position.z);

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            if (Application.isBatchMode)
            {
                enabled = false;
            }
#endif
            if (marker != null)
            {
                marker.SetActive(false);
            }

            player = GetComponent<BeaconRushPlayer>();
            block = new MaterialPropertyBlock();
            lastPosition = transform.position;
        }

        private void OnEnable() => Live.Add(this);

        private void OnDisable() => Live.Remove(this);

        private void LateUpdate()
        {
            if (player == null || !player.IsSpawned)
            {
                return;
            }

            int slot = PlayerPalette.SlotFor(player.OwnerClientId, CurrentBoardIds());
            if (slot != appliedSlot && body != null)
            {
                appliedSlot = slot;
                body.GetPropertyBlock(block);
                block.SetColor(ColorId, PlayerPalette.Colour(slot));
                body.SetPropertyBlock(block);
            }

            bool local = player.IsOwner && player.IsClient;
            if (marker != null && marker.activeSelf != local)
            {
                marker.SetActive(local);
            }

            Vector3 position = transform.position;
            Vector3 moved = position - lastPosition;
            moved.y = 0f;
            if (heading != null && moved.sqrMagnitude > 1e-5f)
            {
                Quaternion target = Quaternion.LookRotation(moved.normalized, Vector3.up);
                heading.rotation = Quaternion.Slerp(heading.rotation, target, 1f - Mathf.Exp(-14f * Time.deltaTime));
            }

            lastPosition = position;
        }

        /// <summary>The score board's client ids, read once per frame for every player; null before the board spawned.</summary>
        private static List<ulong> CurrentBoardIds()
        {
            ScoreBoard board = ScoreBoard.Instance;
            if (board == null || !board.IsSpawned)
            {
                return null;
            }

            if (boardIdsFrame != Time.frameCount)
            {
                boardIdsFrame = Time.frameCount;
                BoardIds.Clear();
                foreach (ScoreEntry entry in board.Entries)
                {
                    BoardIds.Add(entry.ClientId);
                }
            }

            return BoardIds;
        }
    }
}
