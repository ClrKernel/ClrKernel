using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClrKernel.Core.Secrets;
/// <summary>
/// Windows Credential Manager (generic credentials) via advapi32. The secret
/// is stored as a UTF-16 blob under target name
/// <c>ClrKernel:&lt;key&gt;</c>, persisted for the local machine's current user.
/// Only instantiated on Windows (see <see cref="OsSecretProvider.TryCreate"/>).
/// </summary>
internal sealed class WindowsCredentialSecretProvider : ISecretProvider {
    public string Name => "credential-manager";
    public bool CanStore => true;

    private static string Target(string key) => OsSecretProvider.ServiceName + ":" + key;

    public bool TryGet(string key, out string secret) {
        secret = null;
        if (!CredRead(Target(key), _credTypeGeneric, 0, out var handle)) {
            return false;
        }
        try {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) {
                secret = string.Empty;
                return true;
            }
            secret = Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2));
            return true;
        } finally {
            CredFree(handle);
        }
    }

    public void Set(string key, string secret) {
        var blob = Encoding.Unicode.GetBytes(secret ?? string.Empty);
        var blobPtr = Marshal.AllocHGlobal(blob.Length == 0 ? 1 : blob.Length);
        try {
            if (blob.Length > 0) {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
            }
            var cred = new CREDENTIAL {
                Type = _credTypeGeneric,
                TargetName = Target(key),
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = _credPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref cred, 0)) {
                throw new InvalidOperationException(
                    "Credential Manager store failed (error " + Marshal.GetLastWin32Error() + ").");
            }
        } finally {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public void Delete(string key) {
        CredDelete(Target(key), _credTypeGeneric, 0);
    }

    private const int _credTypeGeneric = 1;
    private const int _credPersistLocalMachine = 2;

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int reservedFlag);

    [DllImport("advapi32", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
