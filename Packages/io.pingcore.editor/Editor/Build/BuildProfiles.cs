using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Profile;

namespace PingCore.Editor.Build
{
    /// <summary>One Build Profile asset of the project, as the Ship section and the command line see it.</summary>
    public sealed class BuildProfileInfo
    {
        public BuildProfileInfo(string path, int buildTarget, int subtarget)
        {
            Path = path;
            BuildTarget = buildTarget;
            Subtarget = subtarget;
        }

        /// <summary>The asset path, <c>Assets/...asset</c>.</summary>
        public string Path { get; }

        /// <summary>The profile's <c>UnityEditor.BuildTarget</c> as a number (<c>StandaloneLinux64</c> is 24).</summary>
        public int BuildTarget { get; }

        /// <summary>The profile's <c>StandaloneBuildSubtarget</c> as a number (<c>Server</c> is 1, <c>Player</c> 2).</summary>
        public int Subtarget { get; }

        /// <summary>The file name without <c>.asset</c>.</summary>
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path ?? string.Empty);

        /// <summary>True for a Linux x86_64 Dedicated Server profile, the only kind a PingCore game server is built from.</summary>
        public bool IsLinuxDedicatedServer => BuildProfiles.IsLinuxDedicatedServer(BuildTarget, Subtarget);

        public override string ToString() => $"{Path} (target {BuildTarget}, subtarget {Subtarget})";
    }

    /// <summary>
    /// The project's Build Profiles. Only a Linux x86_64 profile on the Dedicated Server subtarget can
    /// build a PingCore game server, so the Ship section lists only those
    /// (<see cref="LinuxDedicatedServer"/>, pure) and <see cref="ServerBuilder"/> refuses any other.
    /// A profile's platform has no public property, so it is read from the asset's serialized fields
    /// (<c>m_BuildTarget</c>, <c>m_Subtarget</c>) through <see cref="SerializedObject"/>.
    /// The platform-only profiles of the Build Profiles window are not assets and are not listed:
    /// the developer creates a Dedicated Server profile (File &gt; Build Profiles, Linux Server, Add Build Profile).
    /// </summary>
    public static class BuildProfiles
    {
        /// <summary><c>BuildTarget.StandaloneLinux64</c>.</summary>
        public const int LinuxTarget = (int)UnityEditor.BuildTarget.StandaloneLinux64;

        /// <summary><c>StandaloneBuildSubtarget.Server</c>.</summary>
        public const int ServerSubtarget = (int)StandaloneBuildSubtarget.Server;

        /// <summary>True for a Linux x86_64 Dedicated Server target and subtarget. Pure.</summary>
        public static bool IsLinuxDedicatedServer(int buildTarget, int subtarget) => buildTarget == LinuxTarget && subtarget == ServerSubtarget;

        /// <summary>The Linux Dedicated Server profiles among <paramref name="profiles"/>, by path (ordinal). Pure.</summary>
        public static IReadOnlyList<BuildProfileInfo> LinuxDedicatedServer(IEnumerable<BuildProfileInfo> profiles)
        {
            return (profiles ?? Enumerable.Empty<BuildProfileInfo>())
                .Where(p => p != null && !string.IsNullOrEmpty(p.Path) && p.IsLinuxDedicatedServer)
                .OrderBy(p => p.Path, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Every Build Profile asset under <c>Assets/</c>.</summary>
        public static IReadOnlyList<BuildProfileInfo> FindAll()
        {
            var found = new List<BuildProfileInfo>();
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(BuildProfile), new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                BuildProfile profile = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
                BuildProfileInfo info = Describe(profile, path);
                if (info != null)
                {
                    found.Add(info);
                }
            }

            return found;
        }

        /// <summary>The project's Linux Dedicated Server profiles.</summary>
        public static IReadOnlyList<BuildProfileInfo> FindLinuxDedicatedServer() => LinuxDedicatedServer(FindAll());

        /// <summary>A loaded profile's platform, or null when it cannot be read.</summary>
        public static BuildProfileInfo Describe(BuildProfile profile, string path)
        {
            if (profile == null)
            {
                return null;
            }

            using (var serialized = new SerializedObject(profile))
            {
                SerializedProperty target = serialized.FindProperty("m_BuildTarget");
                SerializedProperty subtarget = serialized.FindProperty("m_Subtarget");
                if (target == null || subtarget == null)
                {
                    return null;
                }

                return new BuildProfileInfo(path, target.intValue, subtarget.intValue);
            }
        }

        /// <summary>Loads the profile at <paramref name="path"/>; null with a sentence when it is missing or not a Linux Dedicated Server profile.</summary>
        public static BuildProfile LoadServerProfile(string path, out string problem)
        {
            problem = Cli.BuildServerArgs.CheckBuildProfilePath(path);
            if (problem != null)
            {
                return null;
            }

            BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
            if (profile == null)
            {
                problem = "build profile not found: " + path;
                return null;
            }

            BuildProfileInfo info = Describe(profile, path);
            if (info == null || !info.IsLinuxDedicatedServer)
            {
                problem = "build profile " + path + " is not a Linux Dedicated Server profile (File > Build Profiles: platform Linux, Dedicated Server)";
                return null;
            }

            return profile;
        }
    }
}
