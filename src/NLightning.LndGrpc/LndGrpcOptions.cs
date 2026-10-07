using System.Net;

namespace NLightning.LndGrpc;

/// <summary>
/// <c>LndGrpc</c>: the subset of LND's gRPC API this node serves (<c>docs/agents/LND_GRPC_PLAN.md</c>, NL-1160), so an
/// LND client can use the node as its backend. Always TLS (a self-signed <c>tls.cert</c> made at the first start, as
/// LND does) and LND-format macaroons (<c>admin.macaroon</c>, <c>readonly.macaroon</c>, <c>invoice.macaroon</c>) in
/// <see cref="DataDirectory"/>.
/// </summary>
/// <remarks>
/// Plain settable properties only: the configuration binding source generator binds into the existing instance and
/// skips init-only members (NativeAOT, NL-338).
/// </remarks>
public sealed class LndGrpcOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "LndGrpc";

    /// <summary>LND's default gRPC port (<c>rpclisten</c>).</summary>
    public const int DefaultPort = 10009;

    /// <summary>The directory name under the configuration directory when <see cref="DataDirectory"/> is unset.</summary>
    public const string DefaultDirectoryName = "lnd-grpc";

    /// <summary>Whether the server runs. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The IP address to listen on; loopback by default.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>The TCP port to listen on (0: any free port, for tests).</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Where <c>tls.cert</c>, <c>tls.key</c>, <c>macaroons.key</c> and the three macaroons live; relative paths resolve
    /// against the configuration directory. Unset: <c>&lt;configPath&gt;/lnd-grpc</c>.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>Extra DNS names for the generated certificate (LND's <c>tlsextradomain</c>).</summary>
    public List<string> TlsExtraDomains { get; set; } = [];

    /// <summary>Extra IP addresses for the generated certificate (LND's <c>tlsextraip</c>).</summary>
    public List<string> TlsExtraIps { get; set; } = [];

    /// <summary>
    /// A PEM CA certificate: when set, every client must also present a certificate it signed (mutual TLS, on top of
    /// the macaroon).
    /// </summary>
    public string? ClientCaPath { get; set; }

    /// <summary>
    /// No macaroon check at all (LND's <c>--no-macaroons</c>): every caller that reaches the port may do anything an
    /// admin macaroon allows. Loopback listeners only; off by default.
    /// </summary>
    public bool AllowNoMacaroons { get; set; }

    /// <summary>Allow the server on mainnet: an admin macaroon moves real funds. Off by default.</summary>
    public bool AllowMainnet { get; set; }

    /// <summary>Expose isolated swap signing, key-ring derivation and tapscript imports. Off by default.</summary>
    public bool EnableSigner { get; set; }

    /// <summary>Additional opt-in for raw swap signing on mainnet.</summary>
    public bool AllowSignerOnMainnet { get; set; }

    /// <summary>
    /// Lets <c>RestoreChannelBackups</c> restore a static channel backup over gRPC (NL-1248): recovery channels whose
    /// peers are asked to force close. Off by default: a restore belongs to the operator (<c>nltg restorechanbackup</c>
    /// over the local IPC), not to a remote client holding an admin macaroon.
    /// </summary>
    public bool AllowChannelBackupRestore { get; set; }

    /// <summary>The most concurrent in-memory chain notification streams.</summary>
    public int MaxChainNotifierRegistrations { get; set; } = 128;

    /// <summary>The most blocks a registration may scan from its height hint (pruned blocks fail explicitly).</summary>
    public int MaxChainNotifierScanBlocks { get; set; } = 1_000_000;

    /// <summary>The most concurrent connections.</summary>
    public int MaxConnections { get; set; } = 16;

    /// <summary>The most nodes <c>DescribeGraph</c> returns (0: all, as LND).</summary>
    public int MaxDescribeGraphNodes { get; set; }

    /// <summary>The most channels <c>DescribeGraph</c> returns (0: all, as LND).</summary>
    public int MaxDescribeGraphEdges { get; set; }

    /// <summary>
    /// How long a <c>ChannelAcceptor</c> client may take to answer an open before it is rejected (LND's
    /// <c>acceptortimeout</c>, 15 s).
    /// </summary>
    public TimeSpan AcceptorTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The most opens one <c>ChannelAcceptor</c> client may have unanswered; more are rejected.</summary>
    public int MaxPendingChannelAccepts { get; set; } = 64;

    /// <summary>
    /// <c>HtlcInterceptor</c>: blocks before the incoming HTLC's expiry at which a held forward is failed back (LND's
    /// <c>DefaultFinalCltvRejectDelta</c>, 19).
    /// </summary>
    public uint InterceptorCltvRejectDelta { get; set; } = 19;

    /// <summary>
    /// <c>HtlcInterceptor</c>: a forward whose incoming HTLC expires within this many blocks is failed with
    /// <c>expiry_too_soon</c> instead of offered (LND's <c>DefaultCltvInterceptDelta</c>, 22; above the reject delta).
    /// </summary>
    public uint InterceptorCltvInterceptDelta { get; set; } = 22;

    /// <summary>The most forwards held for the <c>HtlcInterceptor</c> client at once; more fail back.</summary>
    public int MaxHeldHtlcs { get; set; } = 1000;

    /// <summary>The configuration errors that refuse the start (the network rule is checked by the validator).</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MaxChainNotifierRegistrations is < 1 or > 4096 || MaxChainNotifierScanBlocks < 1)
            errors.Add($"{SectionName}: invalid chain notifier limits.");
        if (Port is < 0 or > 65535)
            errors.Add($"{SectionName}:{nameof(Port)} must be a TCP port.");
        if (MaxConnections is < 1 or > 1024)
            errors.Add($"{SectionName}:{nameof(MaxConnections)} must be 1 to 1024.");
        if (MaxDescribeGraphNodes < 0 || MaxDescribeGraphEdges < 0)
            errors.Add($"{SectionName}: the DescribeGraph limits cannot be negative.");
        if (!IPAddress.TryParse(ListenAddress, out var address))
            errors.Add($"{SectionName}:{nameof(ListenAddress)} '{ListenAddress}' is not an IP address.");
        else if (AllowNoMacaroons && !IPAddress.IsLoopback(address))
            errors.Add($"{SectionName}:{nameof(AllowNoMacaroons)} is allowed on a loopback listener only: without "
                     + $"macaroons anyone who reaches {ListenAddress} could spend from the node.");
        foreach (var ip in TlsExtraIps)
        {
            if (!IPAddress.TryParse(ip, out _))
                errors.Add($"{SectionName}:{nameof(TlsExtraIps)} '{ip}' is not an IP address.");
        }

        if (AcceptorTimeout <= TimeSpan.Zero || AcceptorTimeout > TimeSpan.FromHours(1))
            errors.Add($"{SectionName}:{nameof(AcceptorTimeout)} must be positive and at most one hour.");
        if (MaxPendingChannelAccepts is < 1 or > 10_000)
            errors.Add($"{SectionName}:{nameof(MaxPendingChannelAccepts)} must be 1 to 10000.");
        if (InterceptorCltvInterceptDelta <= InterceptorCltvRejectDelta)
            errors.Add($"{SectionName}:{nameof(InterceptorCltvInterceptDelta)} must be above "
                     + $"{nameof(InterceptorCltvRejectDelta)}.");
        if (MaxHeldHtlcs is < 1 or > 100_000)
            errors.Add($"{SectionName}:{nameof(MaxHeldHtlcs)} must be 1 to 100000.");
        if (!string.IsNullOrWhiteSpace(ClientCaPath) && !File.Exists(ClientCaPath))
            errors.Add($"{SectionName}:{nameof(ClientCaPath)} '{ClientCaPath}' does not exist.");
        return errors;
    }

    /// <summary>The data directory: <see cref="DataDirectory"/> resolved against <paramref name="configPath"/>.</summary>
    public string ResolveDataDirectory(string configPath) =>
        string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(configPath, DefaultDirectoryName)
            : Path.IsPathRooted(DataDirectory)
                ? DataDirectory
                : Path.Combine(configPath, DataDirectory);
}