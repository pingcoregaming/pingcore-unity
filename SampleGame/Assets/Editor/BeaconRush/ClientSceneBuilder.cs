using System;
using System.Collections.Generic;
using System.IO;
using BeaconRush.Hosting;
using BeaconRush.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace BeaconRush.Editor
{
    /// <summary>
    /// Generates the client's assets from code, so they never drift from it:
    /// <list type="bullet">
    /// <item><c>Assets/Client/Settings/PingCoreClientSettings.asset</c>: created with EMPTY app ids (Window &gt; PingCore,
    /// Connect writes the developer's own fleet app id locally; a rerun keeps it) and always an EMPTY heartbeat token (the
    /// build guard fails any build that carries one until the Editor plugin can confirm its scope); the Discovery URL is the
    /// SDK's constant, not a field;</item>
    /// <item><c>Assets/Client/Scenes/Client.unity</c>: <c>BeaconRushClient</c> (<see cref="BeaconRushNetwork"/> with the three
    /// network prefabs, <see cref="GameServerRuntime"/>, <see cref="ListenHost"/>, <c>ClientBootstrap</c> with the settings and the
    /// self-host app's placeholder id, <c>ClientUi</c> with the UI document below, <c>MatchView</c> with the camera),
    /// <c>Main Camera</c> (orthographic, looking down on the arena at 58 degrees), <c>Directional Light</c>, the <c>Arena</c>
    /// (<see cref="ArenaBuilder"/>) and <c>Client UI</c>, an INACTIVE object with a <see cref="UIDocument"/> that
    /// <c>ClientUi</c> switches on only for an interactive player, so batchmode and a headless process never build a panel;</item>
    /// <item><c>Assets/Client/UI/BeaconRushPanel.asset</c>: the UI Toolkit <see cref="PanelSettings"/> (the theme
    /// <c>Layout/BeaconRushTheme.tss</c>, scaled from 1280 x 720), used with <c>Layout/ClientMenu.uxml</c>.</item>
    /// </list>
    /// Existing files are brought in line rather than recreated (new scene objects get random file ids), so a rerun with no
    /// code change leaves them byte-identical. The client components live in <c>BeaconRush.Client</c>, which this editor
    /// assembly does not reference (it is absent while the editor compiles for a dedicated server target), so they are
    /// found by name. Headless: <c>-batchmode -nographics -quit -executeMethod BeaconRush.Editor.ClientSceneBuilder.Run</c>.
    /// </summary>
    public static class ClientSceneBuilder
    {
        public const string SceneFolder = "Assets/Client/Scenes";
        public const string SettingsFolder = "Assets/Client/Settings";
        public const string SettingsPath = SettingsFolder + "/PingCoreClientSettings.asset";
        public const string PlaceholderPublicId = "dscp_00000000000000000000000000000000";
        public const string PanelSettingsPath = "Assets/Client/UI/BeaconRushPanel.asset";
        public const string ThemePath = "Assets/Client/UI/Layout/BeaconRushTheme.tss";
        public const string LayoutPath = "Assets/Client/UI/Layout/ClientMenu.uxml";

        /// <summary>The camera's downward pitch, in degrees: steep enough to read as top-down, shallow enough to show the walls.</summary>
        public const float CameraPitch = 58f;

        /// <summary>The orthographic size that frames the arena's depth (<c>MatchView</c> widens it on narrow windows).</summary>
        public const float CameraSize = 12.5f;

        private const string LogPrefix = "[ClientSceneBuilder] ";
        private const string ClientAssembly = "BeaconRush.Client";
        private const string RootName = "BeaconRushClient";
        private const string CameraName = "Main Camera";
        private const string LightName = "Directional Light";
        private const string UiName = "Client UI";

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

            Debug.Log(LogPrefix + (exitCode == 0 ? "PASS: client assets generated" : "FAIL: see the exception above"));
            EditorApplication.Exit(exitCode);
        }

        [MenuItem("Beacon Rush/Regenerate Client Scene and Settings")]
        public static void Generate()
        {
            EnsureFolder(SceneFolder);
            EnsureFolder(SettingsFolder);
            WriteSettings();
            WritePanelSettings();
            WriteScene();
            AssetDatabase.SaveAssets();
            Debug.Log(LogPrefix + "wrote " + SettingsPath + ", " + PanelSettingsPath + " and " + ClientBuildArgs.ScenePath);
        }

        private static void WriteSettings()
        {
            ScriptableObject settings = AssetDatabase.LoadAssetAtPath<ScriptableObject>(SettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance("PingCoreClientSettings");
                if (settings == null)
                {
                    throw new InvalidOperationException("PingCoreClientSettings is not a known ScriptableObject type");
                }

                AssetDatabase.CreateAsset(settings, SettingsPath);
            }

            // The app ids are never written here: the committed asset ships them empty, and each developer's Window >
            // PingCore, Connect writes their own fleet's id locally (the public export refuses an asset that holds one). A
            // rerun keeps whatever Connect wrote, and always empties the heartbeat token: a listen host reads its token from
            // PINGCORE_DISCOVERY_TOKEN.
            using (var serialized = new SerializedObject(settings))
            {
                SetString(serialized, "openRegistrationHeartbeatToken", string.Empty);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            AssetDatabase.ImportAsset(SettingsPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void WritePanelSettings()
        {
            ThemeStyleSheet theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme == null)
            {
                throw new FileNotFoundException("the UI theme is missing: " + ThemePath);
            }

            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.name = Path.GetFileNameWithoutExtension(PanelSettingsPath);
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            }

            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1280, 720);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.sortingOrder = 0;
            panel.clearColor = false;
            panel.targetTexture = null;
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssetIfDirty(panel);
        }

        private static void WriteScene()
        {
            string path = ClientBuildArgs.ScenePath;
            bool exists = AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null;
            Scene scene = exists
                ? EditorSceneManager.OpenScene(path, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Loaded after the scene opens: a single-mode open unloads unreferenced assets, which would turn an
            // earlier-loaded settings object into a destroyed one and save a missing reference.
            ScriptableObject settings = AssetDatabase.LoadAssetAtPath<ScriptableObject>(SettingsPath);
            if (settings == null)
            {
                throw new IOException("could not load " + SettingsPath);
            }
            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            VisualTreeAsset layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (panel == null || layout == null)
            {
                throw new IOException("could not load " + PanelSettingsPath + " or " + LayoutPath);
            }

            var keep = new HashSet<string>(StringComparer.Ordinal) { RootName, CameraName, LightName, ArenaBuilder.RootName, UiName };
            var roots = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (GameObject candidate in scene.GetRootGameObjects())
            {
                if (keep.Contains(candidate.name) && !roots.ContainsKey(candidate.name))
                {
                    roots[candidate.name] = candidate;
                }
                else
                {
                    Object.DestroyImmediate(candidate);
                }
            }

            // Look at a point a little beyond the centre, so the arena sits low in the frame, clear of the HUD along the top.
            Quaternion look = Quaternion.Euler(CameraPitch, 0f, 0f);
            Vector3 target = new Vector3(0f, 0f, 1.6f);
            GameObject cameraObject = Root(roots, CameraName);
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(target - look * Vector3.forward * 45f, look);
            Camera camera = Ensure<Camera>(cameraObject);
            camera.orthographic = true;
            camera.orthographicSize = CameraSize;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.045f, 0.07f, 1f);
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 100f;

            GameObject lightObject = Root(roots, LightName);
            lightObject.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(52f, -38f, 0f));
            Light light = Ensure<Light>(lightObject);
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f, 1f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.55f;

            // Flat ambient, no skybox, no environment reflections and no fog: the arena is lit the same on every machine
            // (MatchView builds the ambient probe from the flat colour at runtime, because nothing is baked).
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.33f, 0.42f, 1f);
            RenderSettings.fog = false;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = null;

            ArenaBuilder.Build(Root(roots, ArenaBuilder.RootName));

            // Inactive in the scene: ClientUi switches it on for an interactive player only.
            GameObject uiObject = Root(roots, UiName);
            uiObject.SetActive(false);
            uiObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            UIDocument document = Ensure<UIDocument>(uiObject);
            document.panelSettings = panel;
            document.visualTreeAsset = layout;
            document.sortingOrder = 0;

            GameObject root = Root(roots, RootName);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
            BeaconRushNetwork network = Ensure<BeaconRushNetwork>(root);
            BeaconRushAssetBuilder.AssignNetworkPrefabs(network);
            Ensure<GameServerRuntime>(root);
            Ensure<ListenHost>(root);
            Component bootstrap = EnsureByName(root, "BeaconRush.Client.ClientBootstrap");
            using (var serialized = new SerializedObject(bootstrap))
            {
                SetReference(serialized, "settings", settings);
                SetString(serialized, "selfHostAppPublicId", PlaceholderPublicId);
                SetReference(serialized, "network", network);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            Component ui = EnsureByName(root, "BeaconRush.Client.UI.ClientUi");
            using (var serialized = new SerializedObject(ui))
            {
                SetReference(serialized, "document", document);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            Component view = EnsureByName(root, "BeaconRush.Client.UI.MatchView");
            using (var serialized = new SerializedObject(view))
            {
                SetReference(serialized, "viewCamera", camera);
                SetFloat(serialized, "baseOrthographicSize", CameraSize);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            using (var check = new SerializedObject(bootstrap))
            {
                if (check.FindProperty("settings").objectReferenceValue == null || check.FindProperty("network").objectReferenceValue == null)
                {
                    throw new InvalidOperationException("ClientBootstrap lost its settings or network reference");
                }
            }

            if (!EditorSceneManager.SaveScene(scene, path))
            {
                throw new IOException("could not save " + path);
            }
        }

        private static GameObject Root(Dictionary<string, GameObject> roots, string name)
        {
            if (!roots.TryGetValue(name, out GameObject found) || found == null)
            {
                found = new GameObject(name);
                roots[name] = found;
            }

            return found;
        }

        private static T Ensure<T>(GameObject target) where T : Component
        {
            // No ?? here: a missing component reads as a Unity fake null, which ?? does not see.
            T component = target.GetComponent<T>();
            if (component == null)
            {
                component = target.AddComponent<T>();
            }

            return component;
        }

        private static Component EnsureByName(GameObject target, string typeName)
        {
            Type type = Type.GetType(typeName + ", " + ClientAssembly);
            if (type == null)
            {
                throw new InvalidOperationException(typeName + " was not found in " + ClientAssembly + " (is the editor compiling for a dedicated server target?)");
            }

            Component component = target.GetComponent(type);
            if (component == null)
            {
                component = target.AddComponent(type);
            }

            return component;
        }

        private static void SetString(SerializedObject serialized, string field, string value)
        {
            SerializedProperty property = serialized.FindProperty(field) ?? throw new InvalidOperationException(serialized.targetObject.GetType().Name + " has no serialized field " + field);
            property.stringValue = value;
        }

        private static void SetFloat(SerializedObject serialized, string field, float value)
        {
            SerializedProperty property = serialized.FindProperty(field) ?? throw new InvalidOperationException(serialized.targetObject.GetType().Name + " has no serialized field " + field);
            property.floatValue = value;
        }

        private static void SetReference(SerializedObject serialized, string field, Object value)
        {
            SerializedProperty property = serialized.FindProperty(field) ?? throw new InvalidOperationException(serialized.targetObject.GetType().Name + " has no serialized field " + field);
            property.objectReferenceValue = value;
        }

        private static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
        }
    }
}
