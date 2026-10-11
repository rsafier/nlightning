using System.Net;

namespace NLightning.LnBackend;

/// <summary>
/// The settings of the hold-invoice gRPC backend (<c>LnBackend</c> section): the Boltz <c>hold.Hold</c> service an
/// unmodified ASP (Second's captaind, the Bark server) drives a Lightning node through, served by this node.
/// </summary>
public sealed class LnBackendOptions
{
    public const string SectionName = "LnBackend";

    public const int DefaultPort = 50052;

    /// <summary>Off unless set: nothing listens (the same shape as the Cashu payment processor).</summary>
    public bool Enabled { get; set; }

    public string ListenAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// The directory holding <c>server.pem</c>/<c>server.key</c> and <c>ca.pem</c>: TLS where every client must present
    /// a certificate <c>ca.pem</c> signed (mutual TLS). Empty, or without <c>ca.pem</c>: no client authentication,
    /// allowed on a loopback address only and only with <c>AllowInsecureLoopback</c> (NL-1089).
    /// </summary>
    public string? TlsDirectory { get; set; }

    /// <summary>Whether a listener without client authentication is allowed on a loopback address (single-user
    /// hosts).</summary>
    public bool AllowInsecureLoopback { get; set; }

    /// <summary>Refused on mainnet unless set (an ASP backend moves real money on the operator's behalf).</summary>
    public bool AllowMainnet { get; set; }

    /// <summary>The most concurrent gRPC clients (each holds streams).</summary>
    public int MaxConnections { get; set; } = 8;

    /// <summary>The maximum xpay retry window in seconds (1 to 3,600); unset requests use at most 60 seconds.</summary>
    public int MaxXpayRetryFor { get; set; } = 300;

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MaxXpayRetryFor is < 1 or > 3600)
            errors.Add($"{SectionName}:{nameof(MaxXpayRetryFor)} must be 1 to 3,600 seconds.");
        if (Port is < 0 or > 65535)
            errors.Add($"{SectionName}:{nameof(Port)} must be a TCP port.");
        if (MaxConnections is < 1 or > 64)
            errors.Add($"{SectionName}:{nameof(MaxConnections)} must be 1 to 64.");
        // Client authentication is mutual TLS (a ca.pem in TlsDirectory); without it the listener must be loopback
        // and opted into, whether it serves h2c or TLS: cln.Node's xpay pays from the node (NL-1089, the NL-998 rules)
        var hasTls = !string.IsNullOrWhiteSpace(TlsDirectory);
        var authenticatesClients = hasTls && File.Exists(Path.Combine(TlsDirectory!, "ca.pem"));
        if (!IPAddress.TryParse(ListenAddress, out var address))
            errors.Add($"{SectionName}:{nameof(ListenAddress)} '{ListenAddress}' is not an IP address.");
        else if (!IPAddress.IsLoopback(address) && !authenticatesClients)
            errors.Add($"{SectionName}:{nameof(ListenAddress)} {ListenAddress} is not loopback: set "
                     + $"{nameof(TlsDirectory)} with server.pem, server.key and ca.pem (mutual TLS); anyone who reaches "
                     + "the port could otherwise settle or cancel hold invoices and pay from the node.");
        else if (!authenticatesClients && !AllowInsecureLoopback)
            errors.Add($"{SectionName} has no client authentication ({(hasTls ? "no ca.pem" : "no TlsDirectory")}): "
                     + "any local process could settle or cancel hold invoices and pay from the node. Set "
                     + $"{nameof(TlsDirectory)} with server.pem, server.key and ca.pem (mutual TLS), or "
                     + $"{nameof(AllowInsecureLoopback)}=true on a single-user host.");
        return errors;
    }
}