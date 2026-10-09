using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeaconRush.Editor;
using BeaconRush.Match;
using BeaconRush.Networking;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

namespace BeaconRush.Client.Tests
{
    /// <summary>
    /// The network surface a live protocol 2 game server shares with this client and with the Quickstart's copy
    /// (<c>Assets/Game/QUICKSTART-SYNC.md</c>), pinned as literals: each prefab keeps its <c>GlobalObjectIdHash</c> and exactly
    /// its <c>NetworkBehaviour</c>s; each of those keeps exactly its <c>NetworkVariableBase</c> fields (name and declared type)
    /// and its RPC methods (name, parameter types and attribute type); the score entry its serialized fields; and
    /// <c>DefaultNetworkPrefabs.asset</c> its GUID and the three prefab GUIDs in order. So the visual components beside them
    /// stay plain <c>MonoBehaviour</c>s, and a change to any of these is a protocol change, never an accident.
    /// </summary>
    public sealed class NetworkSurfaceTests
    {
        private const string DefaultNetworkPrefabsGuid = "f011378a04d590f45a3a2cd8532a3a64";

        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>Every NetworkBehaviour on the three prefabs, with its NetworkVariables and RPCs, each list sorted ordinally.</summary>
        private static readonly (Type Behaviour, string[] Variables, string[] Rpcs)[] Pinned =
        {
            (typeof(NetworkTransform),
                new string[0],
                new[]
                {
                    "SetStateClientRpc(Vector3, Quaternion, Vector3, Boolean): RpcAttribute",
                    "SetStateServerRpc(Vector3, Quaternion, Vector3, Boolean): RpcAttribute",
                }),
            (typeof(BeaconRushPlayer),
                new string[0],
                new[] { "SubmitInputRpc(Vector2): RpcAttribute" }),
            (typeof(Beacon),
                new string[0],
                new string[0]),
            (typeof(ScoreBoard),
                new[]
                {
                    "entries: NetworkList<ScoreEntry>",
                    "inSession: NetworkVariable<Boolean>",
                    "phase: NetworkVariable<Byte>",
                    "secondsLeft: NetworkVariable<Int32>",
                    "winner: NetworkVariable<UInt64>",
                },
                new string[0]),
        };

        [TestCase(BeaconRushAssetBuilder.PlayerPrefabPath, 1393086797L, new[] { "NetworkTransform", "BeaconRushPlayer" })]
        [TestCase(BeaconRushAssetBuilder.BeaconPrefabPath, 3048181459L, new[] { "Beacon" })]
        [TestCase(BeaconRushAssetBuilder.ScoreBoardPrefabPath, 2432384827L, new[] { "ScoreBoard" })]
        public void APrefabKeepsItsHashAndItsNetworkBehaviours(string path, long hash, string[] behaviours)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            NetworkObject networkObject = prefab.GetComponent<NetworkObject>();
            using (var serialized = new SerializedObject(networkObject))
            {
                Assert.That(serialized.FindProperty("GlobalObjectIdHash").longValue, Is.EqualTo(hash), path);
            }

            List<string> found = prefab.GetComponentsInChildren<NetworkBehaviour>(true).Select(b => b.GetType().Name).ToList();
            Assert.That(found, Is.EqualTo(behaviours), path);
        }

        [Test]
        public void EveryNetworkBehaviourOnThePrefabsHasItsSurfacePinned()
        {
            List<Type> onPrefabs = new[] { BeaconRushAssetBuilder.PlayerPrefabPath, BeaconRushAssetBuilder.BeaconPrefabPath, BeaconRushAssetBuilder.ScoreBoardPrefabPath }
                .SelectMany(path => AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<NetworkBehaviour>(true))
                .Select(b => b.GetType())
                .ToList();
            Assert.That(onPrefabs, Is.EquivalentTo(Pinned.Select(p => p.Behaviour)));
        }

        [TestCaseSource(nameof(PinnedSurfaces))]
        public void ANetworkBehaviourKeepsExactlyItsNetworkVariablesAndRpcs(Type behaviour, string[] variables, string[] rpcs)
        {
            Assert.That(NetworkVariablesOf(behaviour), Is.EqualTo(variables), behaviour.Name + " NetworkVariables");
            Assert.That(RpcsOf(behaviour), Is.EqualTo(rpcs), behaviour.Name + " RPCs");
        }

        [Test]
        public void AScoreEntryKeepsItsSerializedFields()
        {
            List<string> fields = typeof(ScoreEntry).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(f => f.MetadataToken)
                .Select(f => f.Name + ": " + TypeName(f.FieldType))
                .ToList();
            Assert.That(fields, Is.EqualTo(new[] { "ClientId: UInt64", "Name: FixedString32Bytes", "Score: Int32" }));
        }

        [Test]
        public void TheDefaultNetworkPrefabListKeepsItsGuidAndTheThreePrefabsInOrder()
        {
            Assert.That(AssetDatabase.AssetPathToGUID(BeaconRushAssetBuilder.DefaultNetworkPrefabsPath), Is.EqualTo(DefaultNetworkPrefabsGuid));
            NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(BeaconRushAssetBuilder.DefaultNetworkPrefabsPath);
            Assert.That(list, Is.Not.Null);
            using (var serialized = new SerializedObject(list))
            {
                Assert.That(serialized.FindProperty("IsDefault").boolValue, Is.True);
                SerializedProperty entries = serialized.FindProperty("List");
                var guids = new List<string>();
                for (int i = 0; i < entries.arraySize; i++)
                {
                    SerializedProperty entry = entries.GetArrayElementAtIndex(i);
                    Assert.That(entry.FindPropertyRelative("Override").enumValueIndex, Is.EqualTo(0), "entry " + i + " overrides nothing");
                    guids.Add(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(entry.FindPropertyRelative("Prefab").objectReferenceValue)));
                }

                Assert.That(guids, Is.EqualTo(new[]
                {
                    "406aeaa5229def8448bcaa6fa0d9b217", // Player.prefab
                    "e0b4527fd30023349a04404ca059feb3", // Beacon.prefab
                    "e16a36c489f9a614a9e654ca48c64c04", // ScoreBoard.prefab
                }));
            }
        }

        [Test]
        public void TheLooksArePlainComponentsOnTheirPrefabs()
        {
            GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(BeaconRushAssetBuilder.PlayerPrefabPath);
            GameObject beacon = AssetDatabase.LoadAssetAtPath<GameObject>(BeaconRushAssetBuilder.BeaconPrefabPath);
            Assert.That(player.GetComponent<PlayerLook>(), Is.Not.Null);
            Assert.That(beacon.GetComponent<BeaconLook>(), Is.Not.Null);
            Assert.That(typeof(NetworkBehaviour).IsAssignableFrom(typeof(PlayerLook)), Is.False);
            Assert.That(typeof(NetworkBehaviour).IsAssignableFrom(typeof(BeaconLook)), Is.False);
        }

        [Test]
        public void TheProtocolIsStillTwo()
        {
            Assert.That(BeaconRushProtocol.Version, Is.EqualTo(2));
            Assert.That(BeaconRushProtocol.Queue, Is.EqualTo("rush-p2"));
        }

        private static IEnumerable<TestCaseData> PinnedSurfaces()
        {
            return Pinned.Select(p => new TestCaseData(p.Behaviour, p.Variables, p.Rpcs).SetName("ANetworkBehaviourKeepsExactlyItsNetworkVariablesAndRpcs(" + p.Behaviour.Name + ")"));
        }

        /// <summary>Every field of a NetworkVariableBase type declared on the behaviour or a base below NetworkBehaviour.</summary>
        private static List<string> NetworkVariablesOf(Type behaviour)
        {
            var found = new List<string>();
            for (Type type = behaviour; type != null && type != typeof(NetworkBehaviour); type = type.BaseType)
            {
                found.AddRange(type.GetFields(Declared)
                    .Where(f => typeof(NetworkVariableBase).IsAssignableFrom(f.FieldType))
                    .Select(f => f.Name + ": " + TypeName(f.FieldType)));
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>Every method carrying an RPC attribute (<c>[Rpc]</c>, or the older <c>[ServerRpc]</c>/<c>[ClientRpc]</c>, both RpcAttributes).</summary>
        private static List<string> RpcsOf(Type behaviour)
        {
            var found = new List<string>();
            for (Type type = behaviour; type != null && type != typeof(NetworkBehaviour); type = type.BaseType)
            {
                foreach (MethodInfo method in type.GetMethods(Declared))
                {
                    foreach (RpcAttribute attribute in method.GetCustomAttributes(typeof(RpcAttribute), false))
                    {
                        string parameters = string.Join(", ", method.GetParameters().Select(p => TypeName(p.ParameterType)));
                        found.Add(method.Name + "(" + parameters + "): " + attribute.GetType().Name);
                    }
                }
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        private static string TypeName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }

            string name = type.Name.Substring(0, type.Name.IndexOf('`'));
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }
    }
}
