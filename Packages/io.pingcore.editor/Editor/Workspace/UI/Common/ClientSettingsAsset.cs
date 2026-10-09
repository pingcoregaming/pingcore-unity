using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PingCore.Discovery.Client;
using UnityEditor;
using UnityEngine;

namespace PingCore.Editor.Workspace.UI.Common
{
    /// <summary>
    /// The plugin owns the project's <see cref="PingCoreClientSettings"/> asset: it uses the one under
    /// <c>Assets/</c> wherever it is (the sample keeps its own at <c>Assets/Client/Settings/</c>), and
    /// only when there is none creates <see cref="DefaultPath"/>, inside a <c>Resources/</c> folder so
    /// <see cref="PingCoreClientSettings.LoadFromResources"/> finds it. The asset is created when the
    /// plugin first writes into it, never just because the window opened. With several assets the
    /// plugin uses <see cref="Choose"/>'s pick and says so.
    /// </summary>
    public static class ClientSettingsAsset
    {
        /// <summary>Where the plugin creates the asset when the project has none.</summary>
        public const string DefaultPath = "Assets/PingCore/Resources/" + PingCoreClientSettings.ResourcesName + ".asset";

        /// <summary>Every settings asset under <c>Assets/</c>, by path (ordinal).</summary>
        public static IReadOnlyList<string> FindPaths()
        {
            return AssetDatabase.FindAssets("t:" + nameof(PingCoreClientSettings), new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p) && AssetDatabase.LoadAssetAtPath<PingCoreClientSettings>(p) != null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The path the plugin uses among <paramref name="paths"/>: <see cref="DefaultPath"/> when present, else the first by ordinal order; null for none. Pure.</summary>
        public static string Choose(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return null;
            }

            return paths.Contains(DefaultPath, StringComparer.Ordinal) ? DefaultPath : paths.OrderBy(p => p, StringComparer.Ordinal).First();
        }

        /// <summary>A sentence when the project holds several assets (every player build should ship one set of ids), else null. Pure.</summary>
        public static string SeveralNote(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count < 2)
            {
                return null;
            }

            return $"The project holds {paths.Count} PingCoreClientSettings assets ({string.Join(", ", paths)}). The plugin uses {Choose(paths)}; delete the others so every build ships the same ids.";
        }

        /// <summary>The asset the plugin uses, or null when the project has none (nothing is created).</summary>
        public static PingCoreClientSettings Find(out string path)
        {
            path = Choose(FindPaths());
            return path == null ? null : AssetDatabase.LoadAssetAtPath<PingCoreClientSettings>(path);
        }

        /// <summary>The asset the plugin uses; creates <paramref name="createPath"/> when the project has none.</summary>
        public static PingCoreClientSettings FindOrCreate(out string path, out bool created, string createPath = DefaultPath)
        {
            PingCoreClientSettings found = Find(out path);
            created = found == null;
            if (found != null)
            {
                return found;
            }

            path = createPath;
            return Create(createPath);
        }

        /// <summary>Creates an empty settings asset at <paramref name="assetPath"/> (an <c>Assets/...asset</c> path), with its folders.</summary>
        public static PingCoreClientSettings Create(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".asset", StringComparison.Ordinal))
            {
                throw new ArgumentException("A settings asset path is Assets/...asset.", nameof(assetPath));
            }

            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            var settings = ScriptableObject.CreateInstance<PingCoreClientSettings>();
            AssetDatabase.CreateAsset(settings, assetPath);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<PingCoreClientSettings>(assetPath);
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
