using System.Collections.Generic;
using BeaconRush.Match;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace BeaconRush.Editor
{
    /// <summary>
    /// The arena the client draws, built from primitives under one <c>Arena</c> object of <c>Client.unity</c>: a 30 x 30 dark
    /// ground, the playing field with a grid of lines, four low walls and four corner pylons with glowing caps. The walls'
    /// inner faces stand where a player's capsule stops: the game server clamps a player's centre to
    /// <see cref="MatchRules.ArenaHalfSize"/> (9) and the capsule's radius is 0.5, so the field is 19 x 19. The geometry is
    /// visual only (no colliders: the game server moves everyone). Children are found by name and brought in line, and any
    /// other child is removed, so a rerun leaves the scene byte-identical.
    /// </summary>
    public static class ArenaBuilder
    {
        /// <summary>The root object's name in the client scene.</summary>
        public const string RootName = "Arena";

        /// <summary>Where a wall's inner face stands: the clamp plus the capsule's radius.</summary>
        public const float FieldHalfSize = MatchRules.ArenaHalfSize + 0.5f;

        private const float WallThickness = 0.5f;
        private const float WallHeight = 0.7f;
        private const float GridStep = 3f;

        /// <summary>Brings <paramref name="arena"/> and its children in line with the arena above.</summary>
        public static void Build(GameObject arena)
        {
            arena.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            arena.transform.localScale = Vector3.one;
            var keep = new HashSet<string>();

            float wallCentre = FieldHalfSize + WallThickness / 2f;
            float wallLength = 2f * (FieldHalfSize + WallThickness);
            // Flat cubes, not the built-in plane: "Plane.fbx" resolved to an upright quad in this editor.
            Piece(arena, keep, "Ground", "Cube.fbx", new Vector3(0f, -0.06f, 0f), new Vector3(30f, 0.1f, 30f), ArtAssets.Ground, false);
            Piece(arena, keep, "Field", "Cube.fbx", new Vector3(0f, 0f, 0f), new Vector3(2f * FieldHalfSize, 0.02f, 2f * FieldHalfSize), ArtAssets.Field, false);
            Piece(arena, keep, "Wall North", "Cube.fbx", new Vector3(0f, WallHeight / 2f, wallCentre), new Vector3(wallLength, WallHeight, WallThickness), ArtAssets.Wall, true);
            Piece(arena, keep, "Wall South", "Cube.fbx", new Vector3(0f, WallHeight / 2f, -wallCentre), new Vector3(wallLength, WallHeight, WallThickness), ArtAssets.Wall, true);
            Piece(arena, keep, "Wall East", "Cube.fbx", new Vector3(wallCentre, WallHeight / 2f, 0f), new Vector3(WallThickness, WallHeight, wallLength), ArtAssets.Wall, true);
            Piece(arena, keep, "Wall West", "Cube.fbx", new Vector3(-wallCentre, WallHeight / 2f, 0f), new Vector3(WallThickness, WallHeight, wallLength), ArtAssets.Wall, true);

            GameObject lines = Child(arena, keep, "Lines");
            lines.transform.localPosition = Vector3.zero;
            lines.transform.localRotation = Quaternion.identity;
            lines.transform.localScale = Vector3.one;
            var lineNames = new HashSet<string>();
            for (float at = -2f * GridStep; at <= 2f * GridStep + 0.01f; at += GridStep)
            {
                string suffix = at.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                Piece(lines, lineNames, "Line X" + suffix, "Cube.fbx", new Vector3(at, 0.015f, 0f), new Vector3(0.06f, 0.01f, 2f * FieldHalfSize), ArtAssets.FieldLine, false);
                Piece(lines, lineNames, "Line Z" + suffix, "Cube.fbx", new Vector3(0f, 0.015f, at), new Vector3(2f * FieldHalfSize, 0.01f, 0.06f), ArtAssets.FieldLine, false);
            }

            RemoveOthers(lines, lineNames);

            foreach ((string name, float x, float z) in new[] { ("Pylon NE", 1f, 1f), ("Pylon NW", -1f, 1f), ("Pylon SE", 1f, -1f), ("Pylon SW", -1f, -1f) })
            {
                GameObject pylon = Piece(arena, keep, name, "Cylinder.fbx", new Vector3(x * wallCentre, 1f, z * wallCentre), new Vector3(1f, 1f, 1f), ArtAssets.Pylon, true);
                var capNames = new HashSet<string>();
                Piece(pylon, capNames, "Cap", "Sphere.fbx", new Vector3(0f, 1.15f, 0f), new Vector3(0.75f, 0.75f, 0.75f), ArtAssets.PylonCap, false);
                RemoveOthers(pylon, capNames);
            }

            RemoveOthers(arena, keep);
        }

        private static GameObject Piece(GameObject parent, HashSet<string> keep, string name, string builtinMesh, Vector3 position, Vector3 scale, string material, bool castShadows)
        {
            GameObject piece = Child(parent, keep, name);
            piece.transform.localPosition = position;
            piece.transform.localRotation = Quaternion.identity;
            piece.transform.localScale = scale;
            MeshFilter filter = piece.GetComponent<MeshFilter>();
            if (filter == null)
            {
                filter = piece.AddComponent<MeshFilter>();
            }

            filter.sharedMesh = Resources.GetBuiltinResource<Mesh>(builtinMesh);
            MeshRenderer renderer = piece.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = piece.AddComponent<MeshRenderer>();
            }

            renderer.sharedMaterial = ArtAssets.Load(material);
            renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return piece;
        }

        private static GameObject Child(GameObject parent, HashSet<string> keep, string name)
        {
            keep.Add(name);
            Transform found = parent.transform.Find(name);
            if (found != null)
            {
                return found.gameObject;
            }

            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            return child;
        }

        private static void RemoveOthers(GameObject parent, HashSet<string> keep)
        {
            // Transform.Find returns the first child of a name, so the first of each kept name stays and later twins go.
            var seen = new HashSet<string>();
            var doomed = new List<GameObject>();
            for (int i = 0; i < parent.transform.childCount; i++)
            {
                GameObject child = parent.transform.GetChild(i).gameObject;
                if (!keep.Contains(child.name) || !seen.Add(child.name))
                {
                    doomed.Add(child);
                }
            }

            foreach (GameObject child in doomed)
            {
                Object.DestroyImmediate(child);
            }
        }
    }
}
