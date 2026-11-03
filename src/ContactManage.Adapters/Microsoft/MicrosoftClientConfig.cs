using System.Text.Json;

namespace ContactManage.Adapters.MicrosoftGraph;

public class MicrosoftAuthException(string message, bool signInAgain = false) : Exception(message)
{
    /// <summary>True when the saved sign-in no longer works (revoked, expired, or the password changed) and the user must sign in again.</summary>
    public bool SignInAgain { get; } = signInAgain;
}

/// <summary>
/// The app registration made for ContactManage in the Microsoft Entra admin center. A desktop app is a public client: there is no
/// client secret, so the file only holds the application (client) id, which identifies the app and is not a user secret. It is still
/// kept out of the repository, like the Google file.
/// </summary>
public sealed class MicrosoftClientConfig
{
    public required string ClientId { get; init; }

    /// <summary>"common" lets both personal Microsoft accounts and work or school accounts sign in.</summary>
    public string Tenant { get; init; } = "common";

    public Uri AuthUri => new($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize");
    public Uri TokenUri => new($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token");

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactManage", "microsoft-client.json");

    /// <summary>Reads <c>{ "client_id": "...", "tenant": "common" }</c>. Throws <see cref="MicrosoftAuthException"/> with a plain explanation when it is missing or wrong.</summary>
    public static MicrosoftClientConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (Directory.Exists(path))
            throw new MicrosoftAuthException($"{path} is a folder, not a file. Move microsoft-client.json out of it.");
        if (!File.Exists(path))
            throw new MicrosoftAuthException($"The Microsoft client file was not found. Ask the person who gave you ContactManage for the file microsoft-client.json and save it as {path} " +
                "(see docs/MICROSOFT.md).");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var id = root.TryGetProperty("client_id", out var c) ? c.GetString() : null;
            if (!Guid.TryParse(id, out _))
                throw new MicrosoftAuthException("The Microsoft client file has no valid client_id (the Application (client) ID, a GUID, from the app registration).");

            // The tenant goes into the sign-in address, so only the four documented words or a tenant GUID are accepted.
            var tenant = root.TryGetProperty("tenant", out var t) ? t.GetString() : null;
            tenant = string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim();
            if (tenant is not ("common" or "organizations" or "consumers") && !Guid.TryParse(tenant, out _))
                throw new MicrosoftAuthException("The Microsoft client file has an unknown tenant. Use \"common\", \"organizations\", \"consumers\" or a tenant id, or leave it out.");
            return new MicrosoftClientConfig { ClientId = id!, Tenant = tenant };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new MicrosoftAuthException("The Microsoft client file could not be read. It must be JSON like { \"client_id\": \"...\" }.");
        }
    }

    public override string ToString() => $"MicrosoftClientConfig {{ ClientId = {ClientId}, Tenant = {Tenant} }}";
}
