using System;
using System.IO;
using BeaconRush.Hosting;
using BeaconRush.Networking;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BeaconRush.Editor
{
    /// <summary>
    /// Generates the Beacon Rush game assets that are built from code, so they never drift from it:
    /// <list type="bullet">
    /// <item><c>Assets/Game/Art/Materials/*.mat</c>: the Standard-shader materials (<see cref="ArtAssets"/>);</item>
    /// <item><c>Assets/Game/Prefabs/Player.prefab</c>, <c>Beacon.prefab</c> and <c>ScoreBoard.prefab</c>: the network prefabs
    /// (<see cref="PrefabBuilder"/>);</item>
    /// <item><c>Assets/DefaultNetworkPrefabs.asset</c>: those three, in that order;</item>
    /// <item><c>Assets/Game/Scenes/Server.unity</c>: one object, <c>BeaconRushServer</c>, with <see cref="BeaconRushNetwork"/>
    /// (holding the three prefabs), <see cref="GameServerRuntime"/> and <see cref="DedicatedServer"/>.</item>
    /// </list>
    /// The generated files are committed. Rerun after changing the components they hold, headless with
    /// <c>-batchmode -nographics -quit -executeMethod BeaconRush.Editor.BeaconRushAssetBuilder.Run</c> or from the
    /// <c>Beacon Rush</c> menu. Rewriting an existing prefab keeps its GUID, so the scenes' references and NGO's prefab
    /// hashes stay stable, and existing scenes are updated in place, so a rerun with no code change leaves every
    /// generated file byte-identical. A client scene builder calls <see cref="AssignNetworkPrefabs"/> for its own
    /// <see cref="BeaconRushNetwork"/>.
    /// <para>
    /// The look of the prefabs is client-side only: plain <c>MonoBehaviour</c>s and child renderers, never a
    /// <c>NetworkBehaviour</c>, <c>NetworkVariable</c> or RPC, so a rewrite keeps every <c>GlobalObjectIdHash</c> and protocol 2
    /// (<c>Assets/Game/QUICKSTART-SYNC.md</c> pins the hashes). The server scene has no arena: clients never load it
    /// (scene management is off) and a dedicated game server draws nothing; the arena is <see cref="ArenaBuilder"/>'s, in
    /// the client scene.
    /// </para>
    /// </summary>
    public static class BeaconRushAssetBuilder
    {
        public const string PrefabFolder = "Assets/Game/Prefabs";
        public const string SceneFolder = "Assets/Game/Scenes";
        public const string PlayerPrefabPath = PrefabFolder + "/Player.prefab";
        public const string BeaconPrefabPath = PrefabFolder + "/Beacon.prefab";
        public const string ScoreBoardPrefabPath = PrefabFolder + "/ScoreBoard.prefab";
        public const string ServerScenePath = SceneFolder + "/Server.unity";
        public const string DefaultNetworkPrefabsPath = "Assets/DefaultNetworkPrefabs.asset";

        private const string LogPrefix = "[BeaconRushAssetBuilder] ";

        /// <summary>The <c>-executeMethod</c> entry point: exits 0 when every asset was written.</summary>
        public static void Run()
        {
            int exitCode = 1;
            try
            {
                Generate();
                exitCode = 0;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            Debug.Log(LogPrefix + (exitCode == 0 ? "PASS: assets generated" : "FAIL: see the exception above"));
            EditorApplication.Exit(exitCode);
        }

        [MenuItem("Beacon Rush/Regenerate Game Prefabs and Server Scene")]
        public static void Generate()
        {
            EnsureFolder(PrefabFolder);
            EnsureFolder(SceneFolder);
            ArtAssets.Generate();
            WriteDefaultNetworkPrefabs(PrefabBuilder.WriteAll());
            WriteServerScene(ServerScenePath, "BeaconRushServer");

            AssetDatabase.SaveAssets();
            Debug.Log(LogPrefix + "wrote " + ArtAssets.Folder + "/*.mat, " + PlayerPrefabPath + ", " + BeaconPrefabPath + ", " + ScoreBoardPrefabPath + ", "
                + DefaultNetworkPrefabsPath + " and " + ServerScenePath);
        }

        /// <summary>
        /// Points a <see cref="BeaconRushNetwork"/> at the three generated prefabs (player, beacon, score board), as every
        /// scene that creates a NetworkManager must: NGO compares the prefab list on connect.
        /// </summary>
        public static void AssignNetworkPrefabs(BeaconRushNetwork network)
        {
            if (network == null)
            {
                throw new ArgumentNullException(nameof(network));
            }

            using (var serialized = new SerializedObject(network))
            {
                SetReference(serialized, BeaconRushNetwork.PlayerPrefabField, LoadPrefab(PlayerPrefabPath));
                SetReference(serialized, BeaconRushNetwork.BeaconPrefabField, LoadPrefab(BeaconPrefabPath));
                SetReference(serialized, BeaconRushNetwork.ScoreBoardPrefabField, LoadPrefab(ScoreBoardPrefabPath));
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        /// <summary>NGO's default prefab list, rewritten to exactly the three prefabs in a fixed order.</summary>
        private static void WriteDefaultNetworkPrefabs(params GameObject[] prefabs)
        {
            NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(DefaultNetworkPrefabsPath);
            if (list == null)
            {
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                AssetDatabase.CreateAsset(list, DefaultNetworkPrefabsPath);
            }

            using (var serialized = new SerializedObject(list))
            {
                SerializedProperty isDefault = serialized.FindProperty("IsDefault");
                SerializedProperty entries = serialized.FindProperty("List");
                if (isDefault == null || entries == null)
                {
                    throw new InvalidOperationException("NetworkPrefabsList has no IsDefault or List field; this NGO version needs another way to write it");
                }

                isDefault.boolValue = true;
                entries.arraySize = prefabs.Length;
                for (int i = 0; i < prefabs.Length; i++)
                {
                    SerializedProperty entry = entries.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("Override").enumValueIndex = 0;
                    entry.FindPropertyRelative("Prefab").objectReferenceValue = prefabs[i];
                    entry.FindPropertyRelative("SourcePrefabToOverride").objectReferenceValue = null;
                    entry.FindPropertyRelative("SourceHashToOverride").longValue = 0;
                    entry.FindPropertyRelative("OverridingTargetPrefab").objectReferenceValue = null;
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorUtility.SetDirty(list);
            AssetDatabase.SaveAssetIfDirty(list);
        }

        /// <summary>
        /// Writes the one-object server scene. An existing scene is opened and brought in line rather than recreated:
        /// Unity gives new scene objects random file ids, so recreating would rewrite the file on every run.
        /// Anything else in the scene, and any component whose script is gone, is removed.
        /// </summary>
        private static void WriteServerScene(string path, string rootName)
        {
            bool exists = AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null;
            Scene scene = exists
                ? EditorSceneManager.OpenScene(path, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = null;
            foreach (GameObject candidate in scene.GetRootGameObjects())
            {
                if (root == null && candidate.name == rootName)
                {
                    root = candidate;
                }
                else
                {
                    Object.DestroyImmediate(candidate);
                }
            }

            if (root == null)
            {
                root = new GameObject(rootName);
            }

            // An earlier ServerBootstrap component is gone; on an old scene it is now a missing script.
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);

            // No ?? here: a missing component reads as a Unity fake null, which ?? does not see.
            BeaconRushNetwork network = root.GetComponent<BeaconRushNetwork>();
            if (network == null)
            {
                network = root.AddComponent<BeaconRushNetwork>();
            }

            AssignNetworkPrefabs(network);
            Ensure<GameServerRuntime>(root);
            Ensure<DedicatedServer>(root);

            if (!EditorSceneManager.SaveScene(scene, path))
            {
                throw new IOException("could not save " + path);
            }
        }

        private static void Ensure<T>(GameObject root) where T : Component
        {
            if (root.GetComponent<T>() == null)
            {
                root.AddComponent<T>();
            }
        }

        private static GameObject LoadPrefab(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                throw new FileNotFoundException("run BeaconRushAssetBuilder.Generate first: " + path + " is missing");
            }

            return prefab;
        }

        /// <summary>Sets a serialized object reference by field name; a field that is gone throws rather than being skipped.</summary>
        internal static void SetReference(SerializedObject serialized, string field, Object value)
        {
            SerializedProperty property = serialized.FindProperty(field);
            if (property == null)
            {
                throw new InvalidOperationException(serialized.targetObject.GetType().Name + " has no serialized field " + field);
            }

            property.objectReferenceValue = value;
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
    }
}
