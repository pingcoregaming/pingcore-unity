using System;
using System.IO;
using System.Reflection;
using BeaconRush.Match;
using BeaconRush.Networking;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BeaconRush.Editor
{
    /// <summary>
    /// The three Beacon Rush network prefabs, written for <see cref="BeaconRushAssetBuilder"/>:
    /// <list type="bullet">
    /// <item><c>Assets/Game/Prefabs/Player.prefab</c>: a <see cref="NetworkObject"/>, a server-authority
    /// <see cref="NetworkTransform"/> (position only), <see cref="BeaconRushPlayer"/>, <see cref="PlayerLook"/>, a capsule
    /// <c>Body</c> and the local player's <c>Marker</c> (a disc and an arrow on a <c>Heading</c> pivot);</item>
    /// <item><c>Assets/Game/Prefabs/Beacon.prefab</c>: a <see cref="NetworkObject"/>, <see cref="Beacon"/>, <see cref="BeaconLook"/>,
    /// a glowing sphere <c>Body</c> and a flat <c>Base</c>;</item>
    /// <item><c>Assets/Game/Prefabs/ScoreBoard.prefab</c>: a <see cref="NetworkObject"/> and <see cref="ScoreBoard"/>.</item>
    /// </list>
    /// Rewriting an existing prefab keeps its GUID, so the scenes' references and NGO's prefab hashes stay stable, and a
    /// rerun with no code change leaves every prefab byte-identical. The look is client-side only: plain
    /// <c>MonoBehaviour</c>s and child renderers with <see cref="ArtAssets"/> materials, never a <c>NetworkBehaviour</c>,
    /// <c>NetworkVariable</c> or RPC (<c>NetworkSurfaceTests</c> pins the network surface).
    /// </summary>
    internal static class PrefabBuilder
    {
        private const string LogPrefix = "[BeaconRushAssetBuilder] ";
        private const string GlobalObjectIdHashField = "GlobalObjectIdHash";

        /// <summary>Writes the player, beacon and score board prefabs and returns them reloaded, in that order.</summary>
        public static GameObject[] WriteAll()
        {
            return new[]
            {
                Write(BeaconRushAssetBuilder.PlayerPrefabPath, BuildPlayer),
                Write(BeaconRushAssetBuilder.BeaconPrefabPath, BuildBeacon),
                Write(BeaconRushAssetBuilder.ScoreBoardPrefabPath, BuildScoreBoard),
            };
        }

        private static void BuildPlayer(GameObject root)
        {
            root.AddComponent<NetworkObject>();
            NetworkTransform transform = root.AddComponent<NetworkTransform>();
            transform.AuthorityMode = NetworkTransform.AuthorityModes.Server;
            transform.SyncPositionY = false;
            transform.SyncRotAngleX = false;
            transform.SyncRotAngleY = false;
            transform.SyncRotAngleZ = false;
            transform.SyncScaleX = false;
            transform.SyncScaleY = false;
            transform.SyncScaleZ = false;
            root.AddComponent<BeaconRushPlayer>();
            GameObject body = AddPrimitive(root, "Body", "Capsule.fbx", new Vector3(0f, 1f, 0f), Vector3.one, ArtAssets.Player, true);

            // The local player's marker: a disc on the ground and an arrow on a pivot that turns toward the last movement.
            var marker = new GameObject("Marker");
            marker.transform.SetParent(root.transform, false);
            AddPrimitive(marker, "Disc", "Cylinder.fbx", new Vector3(0f, 0.02f, 0f), new Vector3(1.5f, 0.01f, 1.5f), ArtAssets.MarkerDisc, false);
            var heading = new GameObject("Heading");
            heading.transform.SetParent(marker.transform, false);
            GameObject arrow = AddPrimitive(heading, "Arrow", "Cube.fbx", new Vector3(0f, 0.04f, 1.3f), new Vector3(0.5f, 0.03f, 0.5f), ArtAssets.Marker, false);
            arrow.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            marker.SetActive(false);

            PlayerLook look = root.AddComponent<PlayerLook>();
            using (var serialized = new SerializedObject(look))
            {
                BeaconRushAssetBuilder.SetReference(serialized, "body", body.GetComponent<MeshRenderer>());
                BeaconRushAssetBuilder.SetReference(serialized, "marker", marker);
                BeaconRushAssetBuilder.SetReference(serialized, "heading", heading.transform);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void BuildBeacon(GameObject root)
        {
            root.AddComponent<NetworkObject>();
            root.AddComponent<Beacon>();
            GameObject body = AddPrimitive(root, "Body", "Sphere.fbx", new Vector3(0f, 0.45f, 0f), new Vector3(0.7f, 0.7f, 0.7f), ArtAssets.Beacon, true);
            AddPrimitive(root, "Base", "Cylinder.fbx", new Vector3(0f, 0.02f, 0f), new Vector3(1.1f, 0.01f, 1.1f), ArtAssets.BeaconBase, false);
            BeaconLook look = root.AddComponent<BeaconLook>();
            using (var serialized = new SerializedObject(look))
            {
                BeaconRushAssetBuilder.SetReference(serialized, "body", body.transform);
                BeaconRushAssetBuilder.SetReference(serialized, "glow", body.GetComponent<MeshRenderer>());
                BeaconRushAssetBuilder.SetReference(serialized, "flashMesh", Resources.GetBuiltinResource<Mesh>("Cylinder.fbx"));
                BeaconRushAssetBuilder.SetReference(serialized, "flashMaterial", ArtAssets.Load(ArtAssets.Flash));
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void BuildScoreBoard(GameObject root)
        {
            root.AddComponent<NetworkObject>();
            root.AddComponent<ScoreBoard>();
        }

        /// <summary>A child with a built-in mesh and one of the <see cref="ArtAssets"/> materials: no collider, no mesh asset of its own.</summary>
        private static GameObject AddPrimitive(GameObject parent, string name, string builtinMesh, Vector3 position, Vector3 scale, string material, bool castShadows)
        {
            var body = new GameObject(name);
            body.transform.SetParent(parent.transform, false);
            body.transform.localPosition = position;
            body.transform.localScale = scale;
            body.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>(builtinMesh);
            MeshRenderer renderer = body.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = ArtAssets.Load(material);
            renderer.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return body;
        }

        private static GameObject Write(string path, Action<GameObject> build)
        {
            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            try
            {
                build(root);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
                if (!saved || prefab == null)
                {
                    throw new IOException("could not save " + path);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            GameObject loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            NetworkObject networkObject = loaded == null ? null : loaded.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                throw new IOException(path + " did not reload with its NetworkObject");
            }

            AssignGlobalObjectIdHash(networkObject, path);
            return loaded;
        }

        /// <summary>
        /// NGO identifies a network prefab by the <c>GlobalObjectIdHash</c> its <c>NetworkObject</c> computes in
        /// <c>OnValidate</c>, which the Editor calls when the prefab is shown in an Inspector, never in a batchmode
        /// run. A prefab saved with hash 0 would not match between game server and client, so the validation is
        /// run here (it is internal to NGO, hence the reflection; Editor-only tooling) and the asset saved.
        /// </summary>
        private static void AssignGlobalObjectIdHash(NetworkObject networkObject, string path)
        {
            MethodInfo validate = typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (validate == null)
            {
                throw new MissingMethodException("NetworkObject.OnValidate is gone; this NGO version needs another way to assign GlobalObjectIdHash");
            }

            validate.Invoke(networkObject, null);
            EditorUtility.SetDirty(networkObject);
            AssetDatabase.SaveAssetIfDirty(networkObject.gameObject);

            using (var serialized = new SerializedObject(networkObject))
            {
                SerializedProperty hash = serialized.FindProperty(GlobalObjectIdHashField);
                if (hash == null || hash.longValue == 0)
                {
                    throw new InvalidOperationException(path + " still has no GlobalObjectIdHash");
                }

                Debug.Log(LogPrefix + path + " GlobalObjectIdHash " + hash.longValue);
            }
        }
    }
}
