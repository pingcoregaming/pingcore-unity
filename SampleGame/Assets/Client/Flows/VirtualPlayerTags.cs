using System;
using System.Collections.Generic;

namespace BeaconRush.Client.Flows
{
    /// <summary>
    /// Unity's Multiplayer Play Mode, as the client's profile choice sees it: whether this Editor is an additional Editor
    /// instance (a virtual player, a separate Unity process launched from the main Editor's Play button), its player
    /// tags (Project Settings &gt; Multiplayer &gt; Play Mode &gt; Player Tags) and its instance id (the <c>-vpId=&lt;id&gt;</c>
    /// argument the main Editor starts it with). The tags are read only in the Editor with Multiplayer Play Mode 2.x installed
    /// (<c>PINGCORE_MPPM</c>, a <c>versionDefines</c> entry of <c>BeaconRush.Client</c>); the instance id in any Editor.
    /// A player build reports a main Editor with neither.
    /// </summary>
    public static class VirtualPlayerTags
    {
        /// <summary>The argument Multiplayer Play Mode starts an additional Editor instance with, followed by its id.</summary>
        public const string InstanceIdFlag = "-vpId";

        /// <summary>True in an additional Editor instance of Multiplayer Play Mode.</summary>
        public static bool IsAdditionalEditor
        {
            get
            {
#if UNITY_EDITOR && PINGCORE_MPPM
                try
                {
                    if (!Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Fall through to the argument.
                }
#endif
#if UNITY_EDITOR
                return InstanceId != null;
#else
                return false;
#endif
            }
        }

        /// <summary>This additional Editor instance's id (the value after <see cref="InstanceIdFlag"/>), or null.</summary>
        public static string InstanceId
        {
            get
            {
#if UNITY_EDITOR
                return InstanceIdFromArgs(Environment.GetCommandLineArgs());
#else
                return null;
#endif
            }
        }

        /// <summary>
        /// The instance id from <c>-vpId=&lt;id&gt;</c> (how Multiplayer Play Mode 2.x starts an additional Editor instance,
        /// for example <c>-vpId=mppm9bd9b493</c>) or <c>-vpId &lt;id&gt;</c>, the flag case-insensitive; null when absent. Pure.
        /// </summary>
        public static string InstanceIdFromArgs(IReadOnlyList<string> argv)
        {
            string prefix = InstanceIdFlag + "=";
            for (int i = 0; argv != null && i < argv.Count; i++)
            {
                string arg = argv[i];
                if (arg == null)
                {
                    continue;
                }

                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring(prefix.Length);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }

                    continue;
                }

                if (string.Equals(arg, InstanceIdFlag, StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Count
                    && !string.IsNullOrWhiteSpace(argv[i + 1]) && !argv[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    return argv[i + 1];
                }
            }

            return null;
        }

        /// <summary>This instance's player tags, or none.</summary>
        public static IReadOnlyList<string> Tags
        {
            get
            {
                var tags = new List<string>();
#if UNITY_EDITOR && PINGCORE_MPPM
                try
                {
                    foreach (string tag in Unity.Multiplayer.PlayMode.CurrentPlayer.Tags)
                    {
                        tags.Add(tag);
                    }
                }
                catch (Exception)
                {
                    // No tags: the profile falls back to an instance slot.
                }
#endif
                return tags;
            }
        }
    }
}
