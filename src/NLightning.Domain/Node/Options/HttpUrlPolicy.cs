using System.Net;

namespace NLightning.Domain.Node.Options;

/// <summary>
/// Which URLs the node's own HTTP clients (fee estimation, the Esplora funding txid source, the accounting price
/// source) may use (NL-678, SECURITY_REVIEW SR-22): <c>https://</c> always; plain <c>http://</c> only to a loopback
/// host (<c>127.0.0.0/8</c>, <c>::1</c>, <c>localhost</c>) or a Tor <c>.onion</c> service (authenticated end to end by
/// Tor), or when the operator explicitly allows it (a self-hosted server on the LAN). An unauthenticated answer could
/// otherwise be rewritten by anyone on the path: a fee rate, a price, a txid list.
/// </summary>
public static class HttpUrlPolicy
{
    private const string OnionSuffix = ".onion";
    private const string LocalhostName = "localhost";
    private const string LocalhostSuffix = ".localhost";

    /// <summary>
    /// Why <paramref name="url"/> may not be used, or null when it may: an absolute <c>https://</c> URL, or an
    /// <c>http://</c> one to a loopback or <c>.onion</c> host, or any <c>http://</c> one with
    /// <paramref name="allowPlainHttp"/>.
    /// </summary>
    /// <param name="url">The configured URL.</param>
    /// <param name="allowPlainHttp">The operator's explicit permission for plain HTTP to any host.</param>
    /// <param name="settingName">The setting's name for the message (e.g. <c>FeeEstimation:Url</c>).</param>
    /// <param name="allowSettingName">The name of the setting that allows plain HTTP, for the message.</param>
    public static string? GetError(string? url, bool allowPlainHttp, string settingName, string allowSettingName)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
         || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return $"{settingName} '{url}' is not an absolute http(s) URL";

        return GetError(uri, allowPlainHttp, settingName, allowSettingName);
    }

    /// <summary>The <see cref="GetError(string?, bool, string, string)"/> check of an absolute URI.</summary>
    public static string? GetError(Uri uri, bool allowPlainHttp, string settingName, string allowSettingName)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return $"{settingName} '{uri}' is not an absolute http(s) URL";

        if (uri.Scheme == Uri.UriSchemeHttps || allowPlainHttp || IsLoopbackOrOnionHost(uri.Host))
            return null;

        return $"{settingName} '{uri}' is plain http:// to {uri.Host}: its answers are not authenticated, so anyone on "
             + $"the path can change them. Use https://, a loopback or .onion host, or set {allowSettingName} true "
             + "for a server you trust on your own network";
    }

    /// <summary>
    /// True for a loopback IP address (<c>127.0.0.0/8</c>, <c>::1</c>, an IPv4-mapped loopback), <c>localhost</c> (or a
    /// name under it, RFC 6761) or a <c>.onion</c> name.
    /// </summary>
    public static bool IsLoopbackOrOnionHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var name = host.Trim().TrimEnd('.');
        if (name.StartsWith('[') && name.EndsWith(']'))
            name = name[1..^1];

        if (IPAddress.TryParse(name, out var address))
            return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

        return name.Equals(LocalhostName, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(LocalhostSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(OnionSuffix, StringComparison.OrdinalIgnoreCase);
    }
}