using System.Net;

namespace NLightning.Cashu.PaymentProcessor;

/// <summary>
/// <c>Cashu:PaymentProcessor</c>: the CDK payment processor that lets a Cashu mint (<c>cdk-mintd</c> with
/// <c>ln_backend = "grpcprocessor"</c>) use this node as its Lightning backend (Cashu plan C1, NL-992).
/// </summary>
/// <remarks>
/// Plain settable properties only: the configuration binding source generator binds into the existing instance and
/// skips init-only members (NativeAOT, NL-338).
/// </remarks>
public sealed class CashuPaymentProcessorOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Cashu:PaymentProcessor";

    /// <summary>The default gRPC port (the one CDK's examples use).</summary>
    public const int DefaultPort = 50051;

    /// <summary>The label put on the invoices and payments the processor makes (accounting, <c>listinvoices</c>).</summary>
    public const string DefaultLabel = "cashu-mint";

    /// <summary>Whether the processor runs. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The IP address to listen on; loopback by default.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>The TCP port to listen on (0: any free port, for tests).</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// A directory with <c>server.pem</c> and <c>server.key</c> (TLS) and, optionally, <c>ca.pem</c> (then every
    /// client must present a certificate it signed: mutual TLS, the layout <c>cdk-mintd</c>'s <c>tls_dir</c> expects).
    /// Empty: plain HTTP/2, allowed on loopback only.
    /// </summary>
    public string? TlsDirectory { get; set; }

    /// <summary>The mint's unit: <c>sat</c> or <c>msat</c>.</summary>
    public string Unit { get; set; } = "sat";

    /// <summary>Allow the processor on mainnet. A mint is a custodial service: the operator owes the ecash issued.</summary>
    public bool AllowMainnet { get; set; }

    /// <summary>The fee reserve quoted for a melt, in millionths of the amount (default 0.5 %, the node's default
    /// fee limit).</summary>
    public uint FeeReservePpm { get; set; } = 5_000;

    /// <summary>The smallest fee reserve quoted for a melt, in msat.</summary>
    public ulong MinFeeReserveMsat { get; set; } = 5_000;

    /// <summary>How long a melt waits for the payment's outcome before answering <c>PENDING</c>, in seconds.</summary>
    public int PaymentTimeoutSeconds { get; set; } = 60;

    /// <summary>The label put on the processor's invoices and payments.</summary>
    public string Label { get; set; } = DefaultLabel;

    /// <summary>Whether <see cref="Unit"/> is msat.</summary>
    public bool IsMsat => string.Equals(Unit, "msat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The problems with these options, empty when they are valid.
    /// </summary>
    /// <param name="isMainnet">Whether the node runs on mainnet.</param>
    public IReadOnlyList<string> GetValidationErrors(bool isMainnet)
    {
        var errors = new List<string>();
        if (!Enabled)
            return errors;

        if (isMainnet && !AllowMainnet)
            errors.Add($"{SectionName} is refused on mainnet unless AllowMainnet is true (a mint is custodial).");
        if (!IPAddress.TryParse(ListenAddress, out var address))
            errors.Add($"{SectionName}:ListenAddress '{ListenAddress}' is not an IP address.");
        else if (!IPAddress.IsLoopback(address) && string.IsNullOrWhiteSpace(TlsDirectory))
            errors.Add($"{SectionName}:ListenAddress {ListenAddress} is not loopback: set TlsDirectory.");
        if (Port is < 0 or > 65_535)
            errors.Add($"{SectionName}:Port {Port} is outside 0-65535 (0: any free port).");
        if (!string.Equals(Unit, "sat", StringComparison.OrdinalIgnoreCase) && !IsMsat)
            errors.Add($"{SectionName}:Unit '{Unit}' is not sat or msat.");
        if (FeeReservePpm > 1_000_000)
            errors.Add($"{SectionName}:FeeReservePpm {FeeReservePpm} is above 1,000,000.");
        if (PaymentTimeoutSeconds is < 1 or > 600)
            errors.Add($"{SectionName}:PaymentTimeoutSeconds {PaymentTimeoutSeconds} is outside 1-600.");
        if (string.IsNullOrWhiteSpace(Label))
            errors.Add($"{SectionName}:Label is empty.");
        if (!string.IsNullOrWhiteSpace(TlsDirectory))
        {
            foreach (var file in new[] { "server.pem", "server.key" })
            {
                if (!File.Exists(Path.Combine(TlsDirectory, file)))
                    errors.Add($"{SectionName}:TlsDirectory has no {file}.");
            }
        }

        return errors;
    }
}