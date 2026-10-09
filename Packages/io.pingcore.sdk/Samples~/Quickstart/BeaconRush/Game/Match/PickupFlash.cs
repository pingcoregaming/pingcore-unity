using UnityEngine;

namespace BeaconRush.Match
{
    /// <summary>
    /// The short burst a picked-up beacon leaves behind: a flat glowing disc that widens and fades to black in
    /// <see cref="Seconds"/>, then removes itself. Client-side only, never networked.
    /// </summary>
    public sealed class PickupFlash : MonoBehaviour
    {
        /// <summary>How long a flash lasts.</summary>
        public const float Seconds = 0.45f;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private MeshRenderer view;
        private MaterialPropertyBlock block;
        private Color colour;
        private float age;

        /// <summary>Starts a flash at <paramref name="position"/> in <paramref name="tint"/>.</summary>
        public static PickupFlash Spawn(Vector3 position, Mesh mesh, Material material, Color tint)
        {
            var root = new GameObject("PickupFlash");
            root.transform.position = new Vector3(position.x, 0.05f, position.z);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            PickupFlash flash = root.AddComponent<PickupFlash>();
            flash.view = renderer;
            flash.colour = tint;
            flash.block = new MaterialPropertyBlock();
            flash.Apply(0f);
            return flash;
        }

        private void Update()
        {
            age += Time.deltaTime;
            if (age >= Seconds)
            {
                Destroy(gameObject);
                return;
            }

            Apply(age / Seconds);
        }

        private void Apply(float progress)
        {
            float eased = 1f - (1f - progress) * (1f - progress);
            float width = Mathf.Lerp(0.6f, 3.2f, eased);
            transform.localScale = new Vector3(width, Mathf.Lerp(0.06f, 0.01f, progress), width);
            float strength = 1f - progress;
            view.GetPropertyBlock(block);
            block.SetColor(ColorId, colour * strength);
            block.SetColor(EmissionId, colour * (2.2f * strength));
            view.SetPropertyBlock(block);
        }
    }
}
