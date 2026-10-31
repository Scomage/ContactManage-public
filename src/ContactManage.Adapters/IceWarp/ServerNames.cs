namespace ContactManage.Adapters.IceWarp;

public static class ServerNames
{
    /// <summary>Hostname from an account's Host value, which is either a URL or a bare host.</summary>
    public static string FromHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        host = host.Trim();
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;
        return host;
    }
}
