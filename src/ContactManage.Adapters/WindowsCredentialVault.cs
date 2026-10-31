using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ContactManage.Core;

namespace ContactManage.Adapters;

/// <summary>
/// Keeps logins in Windows Credential Manager. Windows encrypts them for the signed-in Windows user, so no other
/// Windows user and no other PC can read them. Entries are named "ContactManage:" plus the caller's key (server and account).
/// </summary>
public sealed class WindowsCredentialVault : ICredentialVault
{
    private const string Prefix = "ContactManage:";
    private const uint TypeGeneric = 1;
    private const uint PersistLocalMachine = 2; // this PC only, never roamed to other PCs
    private const int ErrorNotFound = 1168;

    public ServerCredentials? Load(string key)
    {
        if (!CredRead(Prefix + key, TypeGeneric, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<Credential>(ptr);
            if (c.UserName is null || c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0) return null;
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return new ServerCredentials(c.UserName, Encoding.Unicode.GetString(bytes));
        }
        finally { CredFree(ptr); }
    }

    public void Save(string key, ServerCredentials credentials)
    {
        var bytes = Encoding.Unicode.GetBytes(credentials.Password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new Credential
            {
                Type = TypeGeneric,
                TargetName = Prefix + key,
                UserName = credentials.UserName,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(Prefix + key, TypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public int DeleteAll()
    {
        var names = Enumerate("");
        foreach (var name in names) CredDelete(name, TypeGeneric, 0);
        return names.Count;
    }

    public IReadOnlyList<string> Keys(string prefix) => Enumerate(prefix).Select(n => n[Prefix.Length..]).ToList();

    /// <summary>Full target names of this app's entries whose key starts with <paramref name="keyPrefix"/>.</summary>
    private static List<string> Enumerate(string keyPrefix)
    {
        if (!CredEnumerate(Prefix + keyPrefix + "*", 0, out var count, out var list))
        {
            if (Marshal.GetLastWin32Error() == ErrorNotFound) return [];
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var names = new List<string>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                var c = Marshal.PtrToStructure<Credential>(item);
                if (c.TargetName is not null) names.Add(c.TargetName);
            }
        }
        finally { CredFree(list); }
        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
