using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PingCore.Editor.Cli
{
    /// <summary>One object whose components include scripts this editor cannot resolve.</summary>
    public sealed class MissingScriptFinding
    {
        public MissingScriptFinding(string assetPath, string objectPath, int count)
        {
            AssetPath = assetPath;
            ObjectPath = objectPath;
            Count = count;
        }

        /// <summary>The scene or prefab, relative to the project (<c>Assets/...</c> or <c>Packages/...</c>).</summary>
        public string AssetPath { get; }

        /// <summary>The object's hierarchy path inside it, root first, separated by <c>/</c>.</summary>
        public string ObjectPath { get; }

        /// <summary>How many of its components have a missing script.</summary>
        public int Count { get; }

        /// <summary><c>&lt;asset&gt;: &lt;object path&gt; (&lt;count&gt;)</c>, for logs and test messages.</summary>
        public override string ToString() => AssetPath + ": " + ObjectPath + " (" + Count + ")";
    }

    /// <summary>What <see cref="MissingScriptScan.ScanScenes"/> does with a scene once it has scanned it.</summary>
    public enum SceneAfterScan
    {
        /// <summary>The scene was loaded before the scan, which neither opened nor closes it.</summary>
        LeaveLoaded,

        /// <summary>The hierarchy held the scene unloaded; the scan loaded it and unloads it again, keeping it in the hierarchy.</summary>
        Unload,

        /// <summary>The hierarchy did not hold the scene; the scan opened it and removes it again.</summary>
        Remove,
    }

    /// <summary>
    /// Finds components whose script does not resolve in the editor's compiled scripts (the script asset is gone, or its
    /// class is not compiled in this editor), in build scenes and every prefab
    /// they depend on. A player build serialises those scenes from the editor's view, so such a component ships as a
    /// missing script and the build still succeeds: the classic case is an editor left on the Dedicated Server subtarget
    /// (its <c>UNITY_SERVER</c> define compiles out every <c>!UNITY_SERVER</c> assembly) building a client, or the other way
    /// round. A component from an assembly that only the build's extra defines switch on (an instrumentation define) reads
    /// as missing too, so such components are added at runtime, never placed in a build scene. A scene that is not loaded
    /// is opened additively and put back afterwards (<see cref="AfterScan"/>): removed from the hierarchy when it was not
    /// in it, unloaded but kept when it was in it unloaded. Loaded scenes are left as they were.
    /// </summary>
    public static class MissingScriptScan
    {
        /// <summary>Every object with a missing script in <paramref name="scenePaths"/> and the prefabs they depend on.</summary>
        public static IReadOnlyList<MissingScriptFinding> ScanScenes(IEnumerable<string> scenePaths)
        {
            var findings = new List<MissingScriptFinding>();
            var prefabs = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string scenePath in scenePaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(scenePath))
                {
                    continue;
                }

                Scene scene = SceneManager.GetSceneByPath(scenePath);
                SceneAfterScan after = AfterScan(scene.IsValid(), scene.IsValid() && scene.isLoaded);
                bool openedHere = after != SceneAfterScan.LeaveLoaded;
                if (openedHere)
                {
                    // Additive: a scene the hierarchy holds unloaded is loaded in place; any other is added.
                    scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                }

                try
                {
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        ScanHierarchy(scenePath, root, findings);
                    }
                }
                finally
                {
                    if (openedHere)
                    {
                        // removeScene false only unloads, so a scene that was in the hierarchy unloaded stays in it.
                        EditorSceneManager.CloseScene(scene, after == SceneAfterScan.Remove);
                    }
                }

                foreach (string dependency in AssetDatabase.GetDependencies(scenePath, true))
                {
                    if (dependency.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        prefabs.Add(dependency);
                    }
                }
            }

            foreach (string prefabPath in prefabs)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab != null)
                {
                    ScanHierarchy(prefabPath, prefab, findings);
                }
            }

            return findings;
        }

        /// <summary>
        /// How to put a scene back after the scan, from its state before it: <paramref name="valid"/> when the hierarchy
        /// held it, <paramref name="loaded"/> when it was loaded too. Pure.
        /// </summary>
        public static SceneAfterScan AfterScan(bool valid, bool loaded) =>
            !valid ? SceneAfterScan.Remove : loaded ? SceneAfterScan.LeaveLoaded : SceneAfterScan.Unload;

        /// <summary>Adds a finding for <paramref name="root"/> and each descendant (inactive ones too) with a missing script.</summary>
        public static void ScanHierarchy(string assetPath, GameObject root, List<MissingScriptFinding> findings)
        {
            if (root == null || findings == null)
            {
                return;
            }

            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                int missing = CountMissing(transform.gameObject);
                if (missing > 0)
                {
                    findings.Add(new MissingScriptFinding(assetPath, PathOf(transform), missing));
                }
            }
        }

        /// <summary>The missing-script components in every scene loaded right now, opening nothing (the Play mode guard).</summary>
        public static int CountInOpenScenes()
        {
            var findings = new List<MissingScriptFinding>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    ScanHierarchy(scene.path, root, findings);
                }
            }

            int total = 0;
            foreach (MissingScriptFinding finding in findings)
            {
                total += finding.Count;
            }

            return total;
        }

        /// <summary>
        /// One line naming each asset with its total and up to <paramref name="maxObjects"/> object paths, or null when
        /// there are no findings. Pure.
        /// </summary>
        public static string Describe(IReadOnlyList<MissingScriptFinding> findings, int maxObjects = 8)
        {
            if (findings == null || findings.Count == 0)
            {
                return null;
            }

            var order = new List<string>();
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            var objects = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (MissingScriptFinding finding in findings)
            {
                if (!totals.ContainsKey(finding.AssetPath))
                {
                    order.Add(finding.AssetPath);
                    totals[finding.AssetPath] = 0;
                    objects[finding.AssetPath] = new List<string>();
                }

                totals[finding.AssetPath] += finding.Count;
                objects[finding.AssetPath].Add(finding.ObjectPath);
            }

            var text = new StringBuilder();
            foreach (string asset in order)
            {
                if (text.Length > 0)
                {
                    text.Append("; ");
                }

                List<string> names = objects[asset];
                text.Append(asset).Append(": ").Append(totals[asset]).Append(totals[asset] == 1 ? " missing script on " : " missing scripts on ");
                int shown = Math.Min(names.Count, Math.Max(1, maxObjects));
                text.Append(string.Join(", ", names.GetRange(0, shown)));
                if (names.Count > shown)
                {
                    text.Append(" and ").Append(names.Count - shown).Append(" more");
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// The components of <paramref name="gameObject"/> that come back null: a script asset that is gone, and also one
        /// that exists but whose class this editor did not compile (a define constraint left it out). The second is the
        /// case that matters and <c>GameObjectUtility.GetMonoBehavioursWithMissingScriptCount</c> reports 0 for it, so it
        /// only counts towards the larger of the two.
        /// </summary>
        private static int CountMissing(GameObject gameObject)
        {
            int nulls = 0;
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    nulls++;
                }
            }

            return Math.Max(nulls, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject));
        }

        private static string PathOf(Transform transform)
        {
            var parts = new List<string>();
            for (Transform current = transform; current != null; current = current.parent)
            {
                parts.Add(current.name);
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
