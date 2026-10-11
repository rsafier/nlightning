namespace NLightning.Domain.Node.Provisioning;

/// <summary>
/// File secrets a locked start received with the node key (NL-1352, <c>docs/agents/LOCKED_START.md</c>): each one
/// given is used from memory and its file is neither read nor written. A secret not given keeps today's file behavior,
/// except that with <see cref="RefuseGeneration"/> material the node would create on its first start (the LND gRPC
/// macaroon root key and TLS pair, the Tor onion service key, extra macaroon root keys, the default macaroon files) is
/// refused instead of written. The values are validated when the key is delivered; consumers trust them.
/// </summary>
public sealed class ProvisionedSecrets
{
    /// <summary>No provisioned secret: every consumer uses its files (a normal start).</summary>
    public static ProvisionedSecrets None { get; } = new();

    /// <summary>The LND gRPC macaroon root key (root key id 0, the bytes of <c>macaroons.key</c>), 32 bytes.</summary>
    public byte[]? LndGrpcMacaroonRootKey { get; init; }

    /// <summary>The LND gRPC server certificate and key (instead of <c>tls.cert</c>/<c>tls.key</c>), and the client CA
    /// (instead of <c>LndGrpc:ClientCaPath</c>) when given.</summary>
    public ProvisionedTls? LndGrpcTls { get; init; }

    /// <summary>The Tor onion service key, <c>ED25519-V3:&lt;base64&gt;</c> (the content of
    /// <c>Node:Tor:OnionServiceKeyFile</c>).</summary>
    public string? TorOnionServiceKey { get; init; }

    /// <summary>The Cashu payment processor's TLS (instead of <c>server.pem</c>/<c>server.key</c>/<c>ca.pem</c> in its
    /// <c>TlsDirectory</c>).</summary>
    public ProvisionedTls? CashuProcessorTls { get; init; }

    /// <summary>The LN backend's TLS (instead of its <c>TlsDirectory</c> files).</summary>
    public ProvisionedTls? LnBackendTls { get; init; }

    /// <summary>
    /// <c>Node:Startup:RequireProvisionedSecrets</c> on a locked start: generated secret material is never written to
    /// disk; a consumer that would create it refuses to start instead.
    /// </summary>
    public bool RefuseGeneration { get; init; }

    /// <summary>Whether any secret was provisioned.</summary>
    public bool HasAny => LndGrpcMacaroonRootKey is not null || LndGrpcTls is not null || TorOnionServiceKey is not null
                       || CashuProcessorTls is not null || LnBackendTls is not null;
}

/// <summary>A TLS server identity in PEM: the certificate, its private key and, optionally, the CA client certificates
/// must chain to (mutual TLS).</summary>
public sealed record ProvisionedTls(string CertificatePem, string KeyPem, string? ClientCaPem);