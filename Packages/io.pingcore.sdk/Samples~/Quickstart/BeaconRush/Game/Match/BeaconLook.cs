using UnityEngine;

namespace BeaconRush.Match
{
    /// <summary>
    /// How a beacon looks on a client (<c>Prefabs/Beacon.prefab</c>): the glowing sphere bobs slowly and its glow breathes,
    /// and when the game server despawns it next to a player (a pickup) it leaves a <see cref="PickupFlash"/> in that
    /// player's colour. A beacon cleared at the end of a match has no player beside it and leaves nothing. A plain
    /// <c>MonoBehaviour</c>, so the network surface (protocol 2) is untouched; it does nothing on a dedicated game server
    /// or in batchmode.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BeaconLook : MonoBehaviour
    {
        /// <summary>How far from a player a despawn still counts as its pickup: the pickup radius plus interpolation lag.</summary>
        public const float PickupSlack = MatchRules.PickupRadius + 1.25f;

        private const float BobHeight = 0.18f;
        private const float BobSpeed = 2.2f;
        private const float GlowSpeed = 3.1f;

        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        [SerializeField]
        [Tooltip("The glowing sphere that bobs.")]
        private Transform body;

        [SerializeField]
        [Tooltip("The sphere's renderer, whose emission breathes.")]
        private Renderer glow;

        [SerializeField]
        [Tooltip("The colour the glow breathes around.")]
        private Color glowColour = new Color(1f, 0.95f, 0.75f, 1f);

        [SerializeField]
        [Tooltip("The mesh of the pickup flash (a flat disc).")]
        private Mesh flashMesh;

        [SerializeField]
        [Tooltip("The emissive material of the pickup flash.")]
        private Material flashMaterial;

        private MaterialPropertyBlock block;
        private Vector3 bodyRest;
        private float phase;

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
            block = new MaterialPropertyBlock();
            bodyRest = body != null ? body.localPosition : Vector3.zero;
            phase = Random.Range(0f, Mathf.PI * 2f);
        }

        private void Update()
        {
            float t = Time.time;
            if (body != null)
            {
                body.localPosition = bodyRest + Vector3.up * (BobHeight * (1f + Mathf.Sin(t * BobSpeed + phase)));
            }

            if (glow != null)
            {
                glow.GetPropertyBlock(block);
                block.SetColor(EmissionId, glowColour * (1.1f + 0.45f * Mathf.Sin(t * GlowSpeed + phase)));
                glow.SetPropertyBlock(block);
            }
        }

        /// <summary>
        /// The game server despawned this beacon. <paramref name="live"/> is false while the network is shutting down, when
        /// nothing is drawn. A pickup flashes in the colour of the nearest player within <see cref="PickupSlack"/>.
        /// </summary>
        public void OnDespawned(bool live)
        {
            if (!enabled || !live || flashMesh == null || flashMaterial == null)
            {
                return;
            }

            Vector2 here = new Vector2(transform.position.x, transform.position.z);
            PlayerLook nearest = null;
            float best = PickupSlack * PickupSlack;
            foreach (PlayerLook candidate in PlayerLook.All)
            {
                float distance = (candidate.Point - here).sqrMagnitude;
                if (candidate.Slot >= 0 && distance <= best)
                {
                    best = distance;
                    nearest = candidate;
                }
            }

            if (nearest != null)
            {
                PickupFlash.Spawn(transform.position, flashMesh, flashMaterial, PlayerPalette.Colour(nearest.Slot));
            }
        }
    }
}
