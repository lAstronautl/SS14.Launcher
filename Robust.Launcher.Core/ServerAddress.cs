namespace Robust.Launcher.Core;

/// <summary>
/// A parsed <c>ss14://</c> / <c>ss14s://</c> server address.
/// </summary>
public sealed record ServerAddress(string Original, Uri StatusUri, string Host, int Port)
{
    public const int DefaultPort = 1212;

    /// <summary>
    /// Parses addresses like <c>ss14://example.com</c>, <c>ss14s://example.com/path</c>, <c>example.com:1212</c>.
    /// </summary>
    public static bool TryParse(string address, out ServerAddress result)
    {
        result = default!;
        address = address.Trim();
        if (address.Length == 0)
            return false;

        if (!address.Contains("://"))
            address = "ss14://" + address;

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        Uri status;
        switch (uri.Scheme)
        {
            case "ss14":
            {
                var port = uri.IsDefaultPort || uri.Port <= 0 ? DefaultPort : uri.Port;
                status = new UriBuilder("http", uri.Host, port, uri.AbsolutePath).Uri;
                break;
            }
            case "ss14s":
                status = new UriBuilder("https", uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? 443 : uri.Port,
                    uri.AbsolutePath).Uri;
                break;
            case "http":
            case "https":
                status = uri;
                break;
            default:
                return false;
        }

        if (!status.AbsoluteUri.EndsWith('/'))
            status = new Uri(status.AbsoluteUri + "/");

        var gamePort = uri.Scheme == "ss14" && !(uri.IsDefaultPort || uri.Port <= 0) ? uri.Port : DefaultPort;
        result = new ServerAddress(address, status, uri.Host, gamePort);
        return true;
    }

    public static ServerAddress Parse(string address)
    {
        if (!TryParse(address, out var result))
            throw new FormatException($"Invalid server address: {address}");
        return result;
    }

    /// <summary>
    /// Address passed to the engine with <c>--connect-address</c>.
    /// If the server advertises a <c>connect_address</c> in /info, that one wins.
    /// </summary>
    public string GetConnectAddress(ServerInfo? info)
    {
        if (!string.IsNullOrWhiteSpace(info?.ConnectAddress))
            return info!.ConnectAddress!;

        return $"udp://{Host}:{Port}";
    }

    public override string ToString() => Original;
}
