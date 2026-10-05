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
    /// The directory holding <c>server.pem</c>/<c>server.key</c> (and <c>ca.pem</c> for client authentication); empty:
    /// HTTP/2 without TLS, allowed on loopback only with <c>AllowInsecureLoopback</c>.
    /// </summary>
    public string? TlsDirectory { get; set; }

    /// <summary>Whether the insecure no-TLS listener is allowed on a loopback address (single-user hosts).</summary>
    public bool AllowInsecureLoopback { get; set; }

    /// <summary>Refused on mainnet unless set (an ASP backend moves real money on the operator's behalf).</summary>
    public bool AllowMainnet { get; set; }

    /// <summary>The most concurrent gRPC clients (each holds streams).</summary>
    public int MaxConnections { get; set; } = 8;

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (Port is < 0 or > 65535)
            errors.Add($"{SectionName}:{nameof(Port)} must be a TCP port.");
        if (MaxConnections is < 1 or > 64)
            errors.Add($"{SectionName}:{nameof(MaxConnections)} must be 1 to 64.");
        if (string.IsNullOrWhiteSpace(TlsDirectory))
        {
            if (!AllowInsecureLoopback)
                errors.Add($"{SectionName}:{nameof(TlsDirectory)} is required (or set "
                         + $"{SectionName}:{nameof(AllowInsecureLoopback)} for a loopback listener without TLS).");
            else if (!IPAddress.Parse(ListenAddress).Equals(IPAddress.Loopback))
                errors.Add($"{SectionName}:{nameof(AllowInsecureLoopback)} needs a loopback "
                         + $"{nameof(ListenAddress)}.");
        }

        return errors;
    }
}