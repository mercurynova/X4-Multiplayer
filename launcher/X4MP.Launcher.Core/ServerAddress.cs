namespace X4MP.Launcher.Core;

/// <summary>
/// One normal form for the address the player types, so the HTTP client and the trust list cannot disagree about
/// what "the same server" means. A bare host:port gets http, a path or query is dropped, the host is lowercased by
/// <see cref="Uri"/> and the result always ends in '/' because every endpoint is relative to it.
/// </summary>
public static class ServerAddress
{
    public static Uri Normalise(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Enter the server address, for example 192.168.1.20:47790.", nameof(address));
        }

        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "That is not a server address. Use host:port, for example 192.168.1.20:47790.", nameof(address));
        }

        var builder = new UriBuilder(uri) { Path = string.Empty, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    /// <summary>The key a server is remembered under: the normalised address without a trailing path.</summary>
    public static string Key(string? address) => Normalise(address).ToString();
}
