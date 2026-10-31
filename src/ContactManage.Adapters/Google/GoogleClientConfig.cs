using System.Text.Json;

namespace ContactManage.Adapters.Google;

/// <summary>
/// The OAuth client Google issued for this app (the JSON downloaded from the Google Cloud console). It identifies the app,
/// not the user, but it is still kept out of the repository and never printed: <see cref="ToString"/> shows only the id.
/// </summary>
public sealed class GoogleClientConfig
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public Uri AuthUri { get; init; } = new("https://accounts.google.com/o/oauth2/v2/auth");
    public Uri TokenUri { get; init; } = new("https://oauth2.googleapis.com/token");

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactManage", "google-client.json");

    /// <summary>Reads Google's "Desktop app" client file. Throws <see cref="GoogleAuthException"/> with a plain explanation when it is missing or wrong.</summary>
    public static GoogleClientConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (Directory.Exists(path))
            throw new GoogleAuthException($"{path} is a folder, not a file. Move the downloaded JSON out of it and name the file google-client.json.");
        if (!File.Exists(path))
            throw new GoogleAuthException($"The Google client file was not found. Ask the person who gave you ContactManage for the file google-client.json and save it as {path} " +
                "(they also need to have added your Google address as a test user; see docs/GMAIL.md).");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("installed", out var c))
                throw new GoogleAuthException("The Google client file is not for a Desktop app. In the Google Cloud console create an OAuth client of type \"Desktop app\".");

            // Only https endpoints: the client secret and the user's tokens travel to them.
            Uri Endpoint(string name, Uri fallback) =>
                c.TryGetProperty(name, out var v) && Uri.TryCreate(v.GetString(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : fallback;

            return new GoogleClientConfig
            {
                ClientId = c.GetProperty("client_id").GetString() ?? throw new GoogleAuthException("The Google client file has no client_id."),
                ClientSecret = c.GetProperty("client_secret").GetString() ?? throw new GoogleAuthException("The Google client file has no client_secret."),
                AuthUri = Endpoint("auth_uri", new("https://accounts.google.com/o/oauth2/v2/auth")),
                TokenUri = Endpoint("token_uri", new("https://oauth2.googleapis.com/token")),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GoogleAuthException("The Google client file could not be read. Download it again from the Google Cloud console.");
        }
    }

    public override string ToString() => $"GoogleClientConfig {{ ClientId = {ClientId} }}";
}
