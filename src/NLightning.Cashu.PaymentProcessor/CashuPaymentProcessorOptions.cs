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
    /// A directory with <c>server.pem</c>, <c>server.key</c> and <c>ca.pem</c>: TLS where every client must present a
    /// certificate <c>ca.pem</c> signed (mutual TLS, the layout <c>cdk-mintd</c>'s <c>tls_dir</c> expects). Empty: plain
    /// HTTP/2 without client authentication, allowed on loopback only and only with
    /// <see cref="AllowInsecureLoopback"/>.
    /// </summary>
    public string? TlsDirectory { get; set; }

    /// <summary>
    /// Allow a loopback listener without client authentication (plain HTTP/2, or TLS without <c>ca.pem</c>): every
    /// local process and user can then pay invoices from the node's channels through <c>MakePayment</c>, as
    /// <c>cdk-mintd</c>'s <c>allow_insecure</c> on its side. For a single-user host only. Off by default
    /// (NL-998).
    /// </summary>
    public bool AllowInsecureLoopback { get; set; }

    /// <summary>The mint's unit: <c>sat</c> or <c>msat</c>.</summary>
    public string Unit { get; set; } = "sat";

    /// <summary>Allow the processor on mainnet. A mint is a custodial service: the operator owes the ecash issued.</summary>
    public bool AllowMainnet { get; set; }

    /// <summary>The fee reserve quoted for a melt, in millionths of the amount (default 0.5 %, the node's default
    /// fee limit).</summary>
    public uint FeeReservePpm { get; set; } = 5_000;

    /// <summary>The smallest fee reserve quoted for a melt, in msat.</summary>
    public ulong MinFeeReserveMsat { get; set; } = 5_000;

    /// <summary>
    /// The largest melt (any method), in sat: a larger quote or melt is refused (NL-1004). Default 1,000,000 sat; raise
    /// it for a mint that serves larger melts.
    /// </summary>
    public ulong MaxPaymentSat { get; set; } = 1_000_000;

    /// <summary>
    /// The largest Lightning fee a melt may pay, in millionths of its amount (at least
    /// <see cref="MinFeeReserveMsat"/>): the mint's <c>max_fee_amount</c> is capped to it (NL-1004). Default 1 %, twice
    /// the quoted reserve.
    /// </summary>
    public uint MaxFeePpm { get; set; } = 10_000;

    /// <summary>
    /// The largest fee an on-chain melt's transaction may pay, in sat: the mint's fee limit is capped to it (NL-1004).
    /// </summary>
    public ulong MaxOnchainFeeSat { get; set; } = 25_000;

    /// <summary>How long a melt waits for the payment's outcome before answering <c>PENDING</c>, in seconds.</summary>
    public int PaymentTimeoutSeconds { get; set; } = 60;

    /// <summary>The label put on the processor's invoices and payments.</summary>
    public string Label { get; set; } = DefaultLabel;

    /// <summary>
    /// Offer BOLT 12 (NUT-25, NL-997): mint quotes as our offers, melts of offers. On by default; reported to the mint
    /// only while offers work on the node (onion messages and route blinding advertised).
    /// </summary>
    public bool Bolt12Enabled { get; set; } = true;

    /// <summary>
    /// Offer on-chain mint and melt quotes (NUT-30, NL-997) from the node's own on-chain wallet: deposits to a fresh
    /// wallet address per mint quote, melts paid by a wallet withdrawal. Off by default: the mint's users then spend
    /// and fill the wallet that also funds the node's channels and keeps the anchors reserve.
    /// </summary>
    public bool OnchainEnabled { get; set; }

    /// <summary>The confirmations a deposit and a melt's transaction need before the mint hears of them.</summary>
    public uint OnchainConfirmations { get; set; } = 3;

    /// <summary>The smallest deposit that counts toward a mint quote, in sat (smaller ones stay in the wallet).</summary>
    public ulong OnchainMinReceiveSat { get; set; } = 1_000;

    /// <summary>The smallest on-chain melt, in sat.</summary>
    public ulong OnchainMinSendSat { get; set; } = 1_000;

    /// <summary>
    /// The confirmation targets (blocks) of the fee options an on-chain melt quote offers, comma-separated: option 0
    /// is the first (the mint's <c>fee_index</c>). Default <c>2,6,144</c>.
    /// </summary>
    public string OnchainFeeTargets { get; set; } = "2,6,144";

    /// <summary>
    /// The fee reserve of an on-chain fee option, in percent of the estimated fee (default 150: the fee may rise
    /// between the quote and the melt). The melt's transaction never pays more than the reserve the mint passes.
    /// </summary>
    public uint OnchainFeeReservePercent { get; set; } = 150;

    /// <summary>The wallet address type handed out for on-chain mint quotes: <c>P2Wpkh</c> (default) or <c>P2Tr</c>.</summary>
    public string OnchainAddressType { get; set; } = "P2Wpkh";

    /// <summary>The parsed <see cref="OnchainFeeTargets"/>, or null when they are invalid.</summary>
    public IReadOnlyList<uint>? GetOnchainFeeTargets()
    {
        var targets = new List<uint>();
        foreach (var part in OnchainFeeTargets.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                         | StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(part, out var target) || target is < 1 or > 1_008)
                return null;
            targets.Add(target);
        }

        return targets.Count is > 0 and <= 8 ? targets : null;
    }

    /// <summary>Whether <see cref="OnchainAddressType"/> asks for taproot addresses.</summary>
    public bool OnchainUsesTaproot => string.Equals(OnchainAddressType, "P2Tr", StringComparison.OrdinalIgnoreCase);

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
        var hasTls = !string.IsNullOrWhiteSpace(TlsDirectory);
        var authenticatesClients = hasTls && File.Exists(Path.Combine(TlsDirectory!, "ca.pem"));
        if (!IPAddress.TryParse(ListenAddress, out var address))
            errors.Add($"{SectionName}:ListenAddress '{ListenAddress}' is not an IP address.");
        else if (!IPAddress.IsLoopback(address) && !authenticatesClients)
            errors.Add($"{SectionName}:ListenAddress {ListenAddress} is not loopback: set TlsDirectory with "
                     + "server.pem, server.key and ca.pem (mutual TLS); anyone who reaches the port could otherwise "
                     + "pay from the node.");
        else if (!authenticatesClients && !AllowInsecureLoopback)
            errors.Add($"{SectionName} has no client authentication ({(hasTls ? "no ca.pem" : "no TlsDirectory")}): "
                     + "any local process could pay from the node. Set TlsDirectory with server.pem, server.key and "
                     + "ca.pem (mutual TLS), or AllowInsecureLoopback=true on a single-user host.");
        if (Port is < 0 or > 65_535)
            errors.Add($"{SectionName}:Port {Port} is outside 0-65535 (0: any free port).");
        if (!string.Equals(Unit, "sat", StringComparison.OrdinalIgnoreCase) && !IsMsat)
            errors.Add($"{SectionName}:Unit '{Unit}' is not sat or msat.");
        if (FeeReservePpm > 1_000_000)
            errors.Add($"{SectionName}:FeeReservePpm {FeeReservePpm} is above 1,000,000.");
        if (MaxPaymentSat is 0 or > 21_000_000UL * 100_000_000UL)
            errors.Add($"{SectionName}:MaxPaymentSat {MaxPaymentSat} is outside 1-2,100,000,000,000,000.");
        if (MaxFeePpm < FeeReservePpm || MaxFeePpm > 1_000_000)
            errors.Add($"{SectionName}:MaxFeePpm {MaxFeePpm} is outside FeeReservePpm ({FeeReservePpm})-1,000,000: the "
                     + "quoted reserve must be payable.");
        if (MaxOnchainFeeSat == 0)
            errors.Add($"{SectionName}:MaxOnchainFeeSat is 0.");
        if (PaymentTimeoutSeconds is < 1 or > 600)
            errors.Add($"{SectionName}:PaymentTimeoutSeconds {PaymentTimeoutSeconds} is outside 1-600.");
        if (string.IsNullOrWhiteSpace(Label))
            errors.Add($"{SectionName}:Label is empty.");
        if (OnchainEnabled)
        {
            if (OnchainConfirmations is < 1 or > 100)
                errors.Add($"{SectionName}:OnchainConfirmations {OnchainConfirmations} is outside 1-100.");
            if (GetOnchainFeeTargets() is null)
                errors.Add($"{SectionName}:OnchainFeeTargets '{OnchainFeeTargets}' is not 1 to 8 comma-separated "
                         + "block targets of 1-1008.");
            if (OnchainFeeReservePercent is < 100 or > 1_000)
                errors.Add($"{SectionName}:OnchainFeeReservePercent {OnchainFeeReservePercent} is outside 100-1000.");
            if (!OnchainUsesTaproot && !string.Equals(OnchainAddressType, "P2Wpkh", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{SectionName}:OnchainAddressType '{OnchainAddressType}' is not P2Wpkh or P2Tr.");
        }

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