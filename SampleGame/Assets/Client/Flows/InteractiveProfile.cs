using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PingCore.Core.Discovery;

namespace BeaconRush.Client.Flows
{
    /// <summary>
    /// The token store profile of the interactive client (<c>pingcore.playerToken.&lt;profile&gt;.&lt;dscp&gt;</c>), so
    /// two copies of the game on one PC are two players, not one. In order: <c>-pingcoreProfile &lt;id&gt;</c> on the
    /// command line; else, in an additional Editor instance of Unity's Multiplayer Play Mode, <c>mppm-&lt;tag&gt;</c> from
    /// its player tag (<see cref="FromVirtualPlayer"/>: the Editor's PlayerPrefs are shared by every instance, so each
    /// virtual player needs its own profile, and a tag keeps it the same player across runs); else the first free
    /// instance slot, claimed for the life of the process, where slot 0 is the empty profile (the one player a single
    /// copy has always been) and slot <c>n</c> is <c>instance-n</c>, so each concurrent copy keeps its own player across
    /// restarts; else, with every slot held, a fresh random id for this session only. Pure: the slot claim and the
    /// virtual player's tags are injected.
    /// </summary>
    public static class InteractiveProfile
    {
        public const string Flag = "-pingcoreProfile";
        public const int MaxSlots = 8;
        public const string SlotPrefix = "instance-";
        public const string VirtualPlayerPrefix = "mppm-";
        public const int MaxLength = 32;

        private static readonly Regex Unsafe = new Regex("[^A-Za-z0-9_-]", RegexOptions.CultureInvariant);
        private static readonly Regex IdShape = new Regex("^[A-Za-z0-9_.:-]{1,100}$", RegexOptions.CultureInvariant);

        /// <summary>How the profile was chosen, for the Home screen.</summary>
        public enum Source
        {
            Argument,
            VirtualPlayer,
            Slot,
            Session,
        }

        /// <summary>The <c>-pingcoreProfile</c> value (case-insensitive flag, the last one wins), cleaned to the store key's characters; null when absent or not an id. Pure.</summary>
        public static string FromArgs(IReadOnlyList<string> argv)
        {
            string found = null;
            for (int i = 0; argv != null && i + 1 < argv.Count; i++)
            {
                if (!string.Equals(argv[i], Flag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = argv[i + 1];
                found = value != null && IdShape.IsMatch(value) && !value.StartsWith("-", StringComparison.Ordinal) ? Clean(value) : null;
            }

            return found;
        }

        /// <summary>
        /// The profile of a Multiplayer Play Mode additional Editor instance: <c>mppm-&lt;first usable tag&gt;</c>, else
        /// <c>mppm-&lt;instance id&gt;</c>, cleaned to the store key's characters; null for the main Editor or outside
        /// Multiplayer Play Mode. An untagged instance still gets a profile of its own: it shares the main Editor's
        /// PlayerPrefs. Pure.
        /// </summary>
        public static string FromVirtualPlayer(bool additionalEditor, IEnumerable<string> tags, string instanceId = null)
        {
            if (!additionalEditor)
            {
                return null;
            }

            foreach (string tag in tags ?? new string[0])
            {
                string cleaned = Usable(tag);
                if (cleaned != null)
                {
                    return Clean(VirtualPlayerPrefix + cleaned);
                }
            }

            string id = Usable(instanceId);
            return id == null ? null : Clean(VirtualPlayerPrefix + id);
        }

        private static string Usable(string value)
        {
            string cleaned = string.IsNullOrWhiteSpace(value) ? null : Unsafe.Replace(value.Trim(), "-");
            return !string.IsNullOrEmpty(cleaned) && cleaned.Trim('-').Length > 0 ? cleaned : null;
        }

        /// <summary>The profile name for instance slot <paramref name="slot"/>: empty for 0, <c>instance-&lt;n&gt;</c> otherwise. Pure.</summary>
        public static string ForSlot(int slot) => slot <= 0 ? string.Empty : SlotPrefix + slot;

        /// <summary>
        /// Chooses the profile: the argument, else the first slot <paramref name="tryClaimSlot"/> grants (it is asked
        /// in order 0, 1, ... and must keep a granted slot for the life of the process), else
        /// <paramref name="sessionId"/>. Pure.
        /// </summary>
        public static string Resolve(string fromArgs, Func<int, bool> tryClaimSlot, Func<string> sessionId, out Source source) =>
            Resolve(fromArgs, null, tryClaimSlot, sessionId, out source);

        /// <summary>As <see cref="Resolve(string, Func{int, bool}, Func{string}, out Source)"/>, with a virtual player's profile (<see cref="FromVirtualPlayer"/>) after the argument. Pure.</summary>
        public static string Resolve(string fromArgs, string fromVirtualPlayer, Func<int, bool> tryClaimSlot, Func<string> sessionId, out Source source)
        {
            if (fromArgs != null)
            {
                source = Source.Argument;
                return fromArgs;
            }

            if (fromVirtualPlayer != null)
            {
                source = Source.VirtualPlayer;
                return fromVirtualPlayer;
            }

            for (int slot = 0; tryClaimSlot != null && slot < MaxSlots; slot++)
            {
                if (tryClaimSlot(slot))
                {
                    source = Source.Slot;
                    return ForSlot(slot);
                }
            }

            source = Source.Session;
            return Clean((sessionId ?? SecureIds.NewId128)());
        }

        private static string Clean(string value)
        {
            string cleaned = Unsafe.Replace(value, "-");
            return cleaned.Length > MaxLength ? cleaned.Substring(0, MaxLength) : cleaned;
        }
    }

    /// <summary>
    /// The instance slots of <see cref="InteractiveProfile"/>: slot <c>n</c> is held by keeping
    /// <c>&lt;folder&gt;/profile-slot-&lt;n&gt;.lock</c> open with no sharing for the life of the process, so the OS
    /// frees it when the process ends, however it ends.
    /// </summary>
    /// <remarks>
    /// <c>FileShare.None</c> is a lock between processes only on Windows. On macOS and Linux .NET takes an advisory
    /// lock that another process is free to ignore, so two copies could claim the same slot and share one player.
    /// Elsewhere the slots are therefore off (<see cref="Supported"/> is false, <see cref="TryClaim"/> never grants
    /// one) and every copy gets the session-only random profile; the client logs that once. The client is built
    /// for Windows only today (<c>Assets/Editor/BeaconRush/ClientBuild.cs</c>).
    /// </remarks>
    public sealed class ProfileSlotLocks : IDisposable
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        /// <summary>True where a slot lock holds across processes (Windows).</summary>
        public static readonly bool Supported = true;
#else
        /// <summary>True where a slot lock holds across processes (Windows).</summary>
        public static readonly bool Supported = false;
#endif

        /// <summary>The one line the client logs when <see cref="Supported"/> is false.</summary>
        public const string UnsupportedNote = "instance slots need a Windows file lock; this copy plays a session-only profile";

        private readonly string folder;
        private FileStream held;

        public ProfileSlotLocks(string folder)
        {
            this.folder = folder;
        }

        /// <summary>Claims <paramref name="slot"/> if no other process holds it. Keeps at most one slot. Never grants one where <see cref="Supported"/> is false.</summary>
        public bool TryClaim(int slot)
        {
            if (!Supported || held != null || string.IsNullOrEmpty(folder))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(folder);
                held = new FileStream(Path.Combine(folder, "profile-slot-" + slot + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            held?.Dispose();
            held = null;
        }
    }
}
