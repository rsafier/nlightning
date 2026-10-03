namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// What <c>PROTOCOLINFO 1</c> says: the authentication methods, the cookie file and Tor's version.
/// </summary>
/// <param name="AuthMethods">The methods (upper case): NULL, HASHEDPASSWORD, COOKIE, SAFECOOKIE.</param>
/// <param name="CookieFile">The cookie's path, when a cookie method is offered.</param>
/// <param name="TorVersion">Tor's version string (e.g. <c>0.4.8.13</c>), when given.</param>
public sealed record TorProtocolInfo(IReadOnlySet<string> AuthMethods, string? CookieFile, string? TorVersion)
{
    /// <summary>
    /// Reads a <c>PROTOCOLINFO</c> reply (<c>AUTH METHODS=... COOKIEFILE="..."</c>, <c>VERSION Tor="..."</c>).
    /// </summary>
    public static TorProtocolInfo Parse(TorControlReply reply)
    {
        var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cookieFile = null;
        string? version = null;
        foreach (var line in reply.Lines)
        {
            if (line.StartsWith("AUTH ", StringComparison.Ordinal))
            {
                var fields = TorControlClient.ParseKeyValues(line[5..]);
                if (fields.TryGetValue("METHODS", out var list))
                    foreach (var method in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        methods.Add(method.ToUpperInvariant());

                if (fields.TryGetValue("COOKIEFILE", out var path))
                    cookieFile = path;
            }
            else if (line.StartsWith("VERSION ", StringComparison.Ordinal))
            {
                var fields = TorControlClient.ParseKeyValues(line[8..]);
                if (fields.TryGetValue("Tor", out var tor))
                    version = tor;
            }
        }

        return new TorProtocolInfo(methods, cookieFile, version);
    }

    /// <summary>
    /// The numeric part of <see cref="TorVersion"/> (<c>0.4.8.13</c> of <c>0.4.8.13 (git-...)</c> or
    /// <c>0.4.9.1-alpha</c>); null when absent or unreadable.
    /// </summary>
    public Version? GetNumericVersion()
    {
        if (string.IsNullOrWhiteSpace(TorVersion))
            return null;

        var text = TorVersion.Split(' ', '-')[0];
        return Version.TryParse(text, out var parsed) ? parsed : null;
    }
}