using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PingCore.Editor.Cli;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace PingCore.Editor.Tests.Cli
{
    /// <summary>
    /// The missing-script scan a build runs on its scenes before it builds: a scene and a prefab are planted in the open
    /// project with a component whose script GUID resolves to nothing (what an editor compiled for the other subtarget
    /// sees), and the scan must name the asset and the object; the same scene without the component is the control.
    /// </summary>
    public sealed class MissingScriptScanTests
    {
        private const string Folder = "Assets/PingCoreMissingScriptScanTest";
        private const string NoSuchScriptGuid = "0badc0de0badc0de0badc0de0badc0de";
        private const string PrefabGuid = "5c0a3f6e8d2b4e719a7c1f3d2e4b6a80";
        private const string YamlHeader = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        [SetUp]
        public void SetUp()
        {
            TearDown();
            AssetDatabase.CreateFolder("Assets", "PingCoreMissingScriptScanTest");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.DeleteAsset(Folder);
            }
        }

        [Test]
        public void APlantedMissingScriptInASceneIsFoundWithTheSceneAndTheObjectNamed()
        {
            string scene = Write("Planted.unity", Scene(withMissingScript: true, prefabInstance: false));

            IReadOnlyList<MissingScriptFinding> findings = MissingScriptScan.ScanScenes(new[] { scene });

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].AssetPath, Is.EqualTo(scene));
            Assert.That(findings[0].ObjectPath, Is.EqualTo("Root/Planted"));
            Assert.That(findings[0].Count, Is.EqualTo(1));
            Assert.That(MissingScriptScan.Describe(findings), Is.EqualTo(scene + ": 1 missing script on Root/Planted"));
            Assert.That(SceneManager.GetSceneByPath(scene).isLoaded, Is.False, "a scene the scan opened is closed again");
            Assert.That(SceneManager.GetSceneByPath(scene).IsValid(), Is.False, "and removed, as the hierarchy did not hold it");
        }

        [Test]
        public void TheSameSceneWithoutTheComponentHasNoFindings()
        {
            string scene = Write("Clean.unity", Scene(withMissingScript: false, prefabInstance: false));

            IReadOnlyList<MissingScriptFinding> findings = MissingScriptScan.ScanScenes(new[] { scene });

            Assert.That(findings, Is.Empty);
            Assert.That(MissingScriptScan.Describe(findings), Is.Null);
        }

        [Test]
        public void AMissingScriptInAPrefabTheSceneDependsOnIsFoundInThePrefab()
        {
            // A prefab asset's root takes the file's name, whatever its m_Name says.
            string prefab = Write("PlantedPrefab.prefab", Prefab(), PrefabGuid);
            string scene = Write("UsesPrefab.unity", Scene(withMissingScript: false, prefabInstance: true));
            Assert.That(AssetDatabase.GetDependencies(scene, true), Does.Contain(prefab), "the planted scene references the prefab");

            IReadOnlyList<MissingScriptFinding> findings = MissingScriptScan.ScanScenes(new[] { scene });

            Assert.That(findings, Has.Some.Matches<MissingScriptFinding>(f => f.AssetPath == prefab && f.ObjectPath == "PlantedPrefab" && f.Count == 1),
                string.Join("; ", findings));
        }

        [Test]
        public void ASceneTheHierarchyHeldUnloadedIsUnloadedAgainNotRemoved()
        {
            string path = Write("HeldUnloaded.unity", Scene(withMissingScript: true, prefabInstance: false));
            Scene held = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                Assert.That(EditorSceneManager.CloseScene(held, false), Is.True, "unloaded, kept in the hierarchy");
                Assert.That(SceneManager.GetSceneByPath(path).IsValid(), Is.True);
                Assert.That(SceneManager.GetSceneByPath(path).isLoaded, Is.False);

                IReadOnlyList<MissingScriptFinding> findings = MissingScriptScan.ScanScenes(new[] { path });

                Assert.That(findings, Has.Count.EqualTo(1), "the scan loaded the scene to read it");
                Scene after = SceneManager.GetSceneByPath(path);
                Assert.That(after.IsValid(), Is.True, "the scene is still in the hierarchy");
                Assert.That(after.isLoaded, Is.False, "and unloaded again");
            }
            finally
            {
                Scene leftover = SceneManager.GetSceneByPath(path);
                if (leftover.IsValid())
                {
                    EditorSceneManager.CloseScene(leftover, true);
                }
            }
        }

        [TestCase(false, false, SceneAfterScan.Remove)]
        [TestCase(true, false, SceneAfterScan.Unload)]
        [TestCase(true, true, SceneAfterScan.LeaveLoaded)]
        public void AScannedSceneIsPutBackAsItWas(bool valid, bool loaded, SceneAfterScan expected)
        {
            Assert.That(MissingScriptScan.AfterScan(valid, loaded), Is.EqualTo(expected));
        }

        [Test]
        public void DescribeTotalsEachAssetAndCapsTheObjectList()
        {
            var findings = new List<MissingScriptFinding>
            {
                new MissingScriptFinding("Assets/A.unity", "X", 2),
                new MissingScriptFinding("Assets/A.unity", "Y/Z", 1),
                new MissingScriptFinding("Assets/B.prefab", "P", 1),
            };

            Assert.That(MissingScriptScan.Describe(findings, 1), Is.EqualTo("Assets/A.unity: 3 missing scripts on X and 1 more; Assets/B.prefab: 1 missing script on P"));
        }

        private static string Write(string name, string yaml, string guid = null)
        {
            string path = Folder + "/" + name;
            File.WriteAllText(path, yaml);
            if (guid != null)
            {
                File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\nPrefabImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static string GameObject(long id, string name, params long[] components)
        {
            string list = string.Empty;
            foreach (long component in components)
            {
                list += "  - component: {fileID: " + component + "}\n";
            }

            return "--- !u!1 &" + id + "\nGameObject:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n"
                + "  m_PrefabAsset: {fileID: 0}\n  serializedVersion: 6\n  m_Component:\n" + list + "  m_Layer: 0\n  m_Name: " + name + "\n"
                + "  m_TagString: Untagged\n  m_Icon: {fileID: 0}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1\n";
        }

        private static string Transform(long id, long gameObject, long father, params long[] children)
        {
            string list = children.Length == 0 ? " []\n" : "\n";
            foreach (long child in children)
            {
                list += "  - {fileID: " + child + "}\n";
            }

            return "--- !u!4 &" + id + "\nTransform:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n"
                + "  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: " + gameObject + "}\n  serializedVersion: 2\n"
                + "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: {x: 0, y: 0, z: 0}\n  m_LocalScale: {x: 1, y: 1, z: 1}\n"
                + "  m_ConstrainProportionsScale: 0\n  m_Children:" + list + "  m_Father: {fileID: " + father + "}\n"
                + "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n";
        }

        private static string MissingBehaviour(long id, long gameObject) =>
            "--- !u!114 &" + id + "\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n"
            + "  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: " + gameObject + "}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n"
            + "  m_Script: {fileID: 11500000, guid: " + NoSuchScriptGuid + ", type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n";

        /// <summary>Root with a child Planted (with or without the missing script), and optionally an instance of the planted prefab.</summary>
        private static string Scene(bool withMissingScript, bool prefabInstance)
        {
            string yaml = YamlHeader
                + GameObject(10, "Root", 11)
                + Transform(11, 10, 0, 21)
                + (withMissingScript ? GameObject(20, "Planted", 21, 22) : GameObject(20, "Planted", 21))
                + Transform(21, 20, 11)
                + (withMissingScript ? MissingBehaviour(22, 20) : string.Empty);
            string roots = "  - {fileID: 11}\n";
            if (prefabInstance)
            {
                yaml += "--- !u!1001 &30\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n    serializedVersion: 3\n"
                    + "    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n    m_RemovedComponents: []\n    m_RemovedGameObjects: []\n"
                    + "    m_AddedGameObjects: []\n    m_AddedComponents: []\n  m_SourcePrefab: {fileID: 100100000, guid: " + PrefabGuid + ", type: 3}\n";
                roots += "  - {fileID: 30}\n";
            }

            return yaml + "--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots:\n" + roots;
        }

        private static string Prefab() =>
            YamlHeader + GameObject(100, "PlantedPrefab", 101, 102) + Transform(101, 100, 0) + MissingBehaviour(102, 100);
    }
}
