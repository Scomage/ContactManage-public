using ContactManage.Core;

namespace ContactManage.Tests;

/// <summary>An in-memory stand-in for Windows Credential Manager.</summary>
public sealed class MemoryVault : ICredentialVault
{
    public Dictionary<string, ServerCredentials> Items { get; } = new();
    public ServerCredentials? Load(string key) => Items.GetValueOrDefault(key);
    public void Save(string key, ServerCredentials credentials) => Items[key] = credentials;
    public void Delete(string key) => Items.Remove(key);
    public int DeleteAll() { var n = Items.Count; Items.Clear(); return n; }
    public IReadOnlyList<string> Keys(string prefix) => Items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}
