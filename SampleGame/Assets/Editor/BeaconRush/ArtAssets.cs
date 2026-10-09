using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BeaconRush.Editor
{
    /// <summary>
    /// The Standard-shader materials of Beacon Rush, written from code to <c>Assets/Game/Art/Materials/</c> so they never drift
    /// from it: the arena (ground, field, field lines, walls, pylons and their glowing caps), the players (white; each body is
    /// tinted at runtime by <c>PlayerLook</c> from <c>PlayerPalette</c>), the beacons and their base, the local player's marker
    /// (a muted disc and a bright arrow) and the pickup flash. An existing material is updated in place, so its GUID stays
    /// and a rerun with no change leaves it byte-identical. <see cref="BeaconRushAssetBuilder"/> writes them before the
    /// prefabs that use them.
    /// </summary>
    public static class ArtAssets
    {
        public const string Folder = "Assets/Game/Art/Materials";

        public const string Ground = "Ground";
        public const string Field = "Field";
        public const string FieldLine = "FieldLine";
        public const string Wall = "Wall";
        public const string Pylon = "Pylon";
        public const string PylonCap = "PylonCap";
        public const string Player = "Player";
        public const string Beacon = "Beacon";
        public const string BeaconBase = "BeaconBase";
        public const string Marker = "Marker";
        public const string MarkerDisc = "MarkerDisc";
        public const string Flash = "Flash";

        private static readonly Spec[] Specs =
        {
            new Spec(Ground, new Color(0.03f, 0.036f, 0.055f), null, 0f, 0f),
            new Spec(Field, new Color(0.16f, 0.18f, 0.24f), null, 0.2f, 0f),
            new Spec(FieldLine, new Color(0.34f, 0.40f, 0.52f), new Color(0.06f, 0.09f, 0.14f), 0.3f, 0f),
            new Spec(Wall, new Color(0.30f, 0.35f, 0.47f), null, 0.45f, 0.1f),
            new Spec(Pylon, new Color(0.20f, 0.23f, 0.31f), null, 0.6f, 0.4f),
            new Spec(PylonCap, new Color(0.38f, 0.84f, 1f), new Color(0.35f, 0.85f, 1.2f), 0.8f, 0f),
            new Spec(Player, Color.white, null, 0.55f, 0f),
            new Spec(Beacon, new Color(1f, 0.93f, 0.62f), new Color(1f, 0.9f, 0.55f), 0.9f, 0f),
            new Spec(BeaconBase, new Color(0.45f, 0.40f, 0.20f), new Color(0.40f, 0.33f, 0.10f), 0.2f, 0f),
            new Spec(Marker, new Color(0.92f, 0.97f, 1f), new Color(0.85f, 0.92f, 1f), 0.4f, 0f),
            new Spec(MarkerDisc, new Color(0.16f, 0.42f, 0.55f), new Color(0.05f, 0.22f, 0.32f), 0.3f, 0f),
            new Spec(Flash, Color.white, Color.white, 0f, 0f),
        };

        /// <summary>Writes every material.</summary>
        public static void Generate()
        {
            EnsureFolder(Folder);
            Shader standard = Shader.Find("Standard") ?? throw new InvalidOperationException("the built-in Standard shader was not found");
            foreach (Spec spec in Specs)
            {
                Write(spec, standard);
            }
        }

        /// <summary>The asset path of a material.</summary>
        public static string PathOf(string name) => Folder + "/" + name + ".mat";

        /// <summary>A generated material; run <see cref="Generate"/> first.</summary>
        public static Material Load(string name)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(PathOf(name));
            if (material == null)
            {
                throw new FileNotFoundException("run ArtAssets.Generate first: " + PathOf(name) + " is missing");
            }

            return material;
        }

        private static void Write(Spec spec, Shader standard)
        {
            string path = PathOf(spec.Name);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(standard) { name = spec.Name };
                AssetDatabase.CreateAsset(material, path);
            }

            if (material.shader != standard)
            {
                material.shader = standard;
            }

            material.SetColor("_Color", spec.Albedo);
            material.SetFloat("_Glossiness", spec.Smoothness);
            material.SetFloat("_Metallic", spec.Metallic);
            if (spec.Emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", spec.Emission.Value);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Color.black);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
        }

        private static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
        }

        private readonly struct Spec
        {
            public Spec(string name, Color albedo, Color? emission, float smoothness, float metallic)
            {
                Name = name;
                Albedo = albedo;
                Emission = emission;
                Smoothness = smoothness;
                Metallic = metallic;
            }

            public string Name { get; }

            public Color Albedo { get; }

            public Color? Emission { get; }

            public float Smoothness { get; }

            public float Metallic { get; }
        }
    }
}
