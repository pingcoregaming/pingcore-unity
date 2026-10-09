using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PingCore.Editor.Workspace.Credentials
{
    /// <summary>
    /// Windows Credential Manager through <c>advapi32</c> (<c>CredWriteW</c>, <c>CredReadW</c>,
    /// <c>CredDeleteW</c>, <c>CredFree</c>): generic credentials, persisted per machine for this
    /// Windows user and protected by DPAPI. The secret is stored as UTF-16LE in the credential
    /// blob and the optional user name in <c>UserName</c>. Every managed and
    /// unmanaged buffer that held a secret is zeroed after the call. Messages carry the Windows
    /// error code only.
    /// </summary>
    public sealed class WindowsCredentialStore : ICredentialStore
    {
        internal const uint CredTypeGeneric = 1;
        internal const uint CredPersistLocalMachine = 2;
        internal const int ErrorNotFound = 1168;

        /// <summary>The largest blob Windows accepts for a generic credential (5 * 512 bytes).</summary>
        internal const int MaxBlobBytes = 2560;

        private const string Comment = "PingCore Editor plugin";

        public CredentialStoreKind Kind => CredentialStoreKind.WindowsCredentialManager;

        public string Description => "Windows Credential Manager (encrypted for this Windows user).";

        /// <summary>True on Windows.</summary>
        public static bool IsSupportedPlatform => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Checks that the store answers: reads a name that does not exist and expects "not found".
        /// Returns null when it works, else a sentence naming the Windows error code.
        /// </summary>
        public static string Probe()
        {
            if (!IsSupportedPlatform)
            {
                return "Windows Credential Manager exists only on Windows.";
            }

            try
            {
                if (CredRead("PingCore/probe/" + Guid.NewGuid().ToString("N"), CredTypeGeneric, 0, out IntPtr found))
                {
                    CredFree(found);
                    return null;
                }

                int error = Marshal.GetLastWin32Error();
                return error == ErrorNotFound ? null : $"Windows Credential Manager did not answer (Windows error {error}).";
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return $"Windows Credential Manager could not be loaded ({e.GetType().Name}).";
            }
        }

        public StoredCredential Read(string target)
        {
            CheckTarget(target);
            if (!CredRead(target, CredTypeGeneric, 0, out IntPtr pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return null;
                }

                throw new CredentialStoreException($"CredReadW failed (Windows error {error}).");
            }

            try
            {
                var native = Marshal.PtrToStructure<NativeCredential>(pointer);
                int size = (int)native.CredentialBlobSize;
                if (size <= 0 || native.CredentialBlob == IntPtr.Zero)
                {
                    return null;
                }

                var bytes = new byte[size];
                try
                {
                    Marshal.Copy(native.CredentialBlob, bytes, 0, size);
                    string secret = Encoding.Unicode.GetString(bytes);
                    string userName = native.UserName == IntPtr.Zero ? null : Marshal.PtrToStringUni(native.UserName);
                    return new StoredCredential(secret, string.IsNullOrEmpty(userName) ? null : userName);
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                    Zero(native.CredentialBlob, size);
                }
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public bool Exists(string target)
        {
            CheckTarget(target);
            if (!CredRead(target, CredTypeGeneric, 0, out IntPtr pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return false;
                }

                throw new CredentialStoreException($"CredReadW failed (Windows error {error}).");
            }

            try
            {
                // Only the blob's size is looked at; the blob itself is zeroed unread before it is freed.
                var native = Marshal.PtrToStructure<NativeCredential>(pointer);
                int size = (int)native.CredentialBlobSize;
                Zero(native.CredentialBlob, size);
                return size > 0 && native.CredentialBlob != IntPtr.Zero;
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public void Write(string target, string secret, string userName = null)
        {
            CheckTarget(target);
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("An empty secret is never stored.", nameof(secret));
            }

            byte[] bytes = Encoding.Unicode.GetBytes(secret);
            if (bytes.Length > MaxBlobBytes)
            {
                Array.Clear(bytes, 0, bytes.Length);
                throw new CredentialStoreException($"The secret is longer than Windows Credential Manager accepts ({MaxBlobBytes} bytes).");
            }

            IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
            IntPtr targetName = Marshal.StringToHGlobalUni(target);
            IntPtr comment = Marshal.StringToHGlobalUni(Comment);
            IntPtr user = userName == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(userName);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var native = new NativeCredential
                {
                    Flags = 0,
                    Type = CredTypeGeneric,
                    TargetName = targetName,
                    Comment = comment,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = CredPersistLocalMachine,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    TargetAlias = IntPtr.Zero,
                    UserName = user,
                };
                if (!CredWrite(ref native, 0))
                {
                    throw new CredentialStoreException($"CredWriteW failed (Windows error {Marshal.GetLastWin32Error()}).");
                }
            }
            finally
            {
                Zero(blob, bytes.Length);
                Array.Clear(bytes, 0, bytes.Length);
                Marshal.FreeHGlobal(blob);
                Marshal.FreeHGlobal(targetName);
                Marshal.FreeHGlobal(comment);
                if (user != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(user);
                }
            }
        }

        public bool Delete(string target)
        {
            CheckTarget(target);
            if (CredDelete(target, CredTypeGeneric, 0))
            {
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }

            throw new CredentialStoreException($"CredDeleteW failed (Windows error {error}).");
        }

        private static void CheckTarget(string target)
        {
            if (!CredentialTargets.IsTarget(target))
            {
                throw new ArgumentException("Not a PingCore credential target.", nameof(target));
            }
        }

        private static void Zero(IntPtr buffer, int length)
        {
            if (buffer == IntPtr.Zero || length <= 0)
            {
                return;
            }

            var zeros = new byte[length];
            Marshal.Copy(zeros, 0, buffer, length);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
        private static extern void CredFree(IntPtr buffer);
    }
}
