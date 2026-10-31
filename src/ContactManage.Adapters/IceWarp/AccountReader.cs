using System.Text.Json;

namespace ContactManage.Adapters.IceWarp;

/// <summary>Non-secret facts about one protocol record in accounts.dat.</summary>
public sealed record AccountRecord(string Id, string TypeName, string? AccountName, string? Host);

/// <summary>
/// Reads accounts.dat (table AccountsJson, one JSON record per row: {"Type": "...", "TypeValue": {...}}).
/// Only AccountName and Host are read. Credentials in the same record are never touched.
/// </summary>
public static class AccountReader
{
    public static Dictionary<string, AccountRecord> Read(string accountsDatPath)
    {
        var result = new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase);
        using var copy = new SqliteCopy(accountsDatPath);
        using var conn = copy.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select key, value from AccountsJson";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            try
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                var root = doc.RootElement;
                var typeName = ShortTypeName(root.GetProperty("Type").GetString());
                var value = root.GetProperty("TypeValue");
                result[key] = new AccountRecord(key, typeName, GetString(value, "AccountName"), GetString(value, "Host"));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Internal format; skip records we cannot parse rather than fail the whole list.
            }
        }
        return result;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // "MailClient.Protocols.CalDav.CalDavAccountConfiguration, MailClient.Protocols.CalDav" -> "CalDavAccountConfiguration"
    internal static string ShortTypeName(string? type)
    {
        var name = (type ?? "").Split(',')[0];
        return name[(name.LastIndexOf('.') + 1)..];
    }
}
