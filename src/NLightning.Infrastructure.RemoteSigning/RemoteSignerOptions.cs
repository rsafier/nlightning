using NLightning.Domain.Signing;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed class RemoteSignerOptions
{
    public string NodeId { get; set; } = NodeSigningContext.DefaultNodeId;
    public string OwnerId { get; set; } = NodeSigningContext.DefaultOwnerId;
    public string SignerId { get; set; } = NodeSigningContext.DefaultSignerId;
    public string SocketPath { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string Network { get; set; } = "Regtest";
    public string? WriterId { get; set; }
    public long? WriterEpoch { get; set; }
    public string? WriterCredential { get; set; }
    public string? ExpectedNodePublicKey { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public void Validate()
    {
        NodeSigningContext.ValidateIdentifier(NodeId, nameof(NodeId));
        NodeSigningContext.ValidateIdentifier(OwnerId, nameof(OwnerId));
        NodeSigningContext.ValidateIdentifier(SignerId, nameof(SignerId));
        _ = NLightning.Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(Network);
        if (string.IsNullOrWhiteSpace(SocketPath) || !Path.IsPathFullyQualified(SocketPath))
            throw new ArgumentException("Signer SocketPath must be absolute.");
        if (AuthToken.Length < 32 || AuthToken.Any(c => c is < (char)33 or > (char)126)) throw new ArgumentException("Signer authentication token must contain at least 32 printable ASCII characters without spaces.");
        if (WriterId is not null || WriterEpoch is not null || WriterCredential is not null)
        {
            NodeSigningContext.ValidateIdentifier(WriterId ?? "", nameof(WriterId));
            if (WriterEpoch is null or <= 0) throw new ArgumentException("A positive installed writer epoch is required.");
            if (WriterCredential is not { Length: >= 32 } || WriterCredential.Any(c => c is < '!' or > '~'))
                throw new ArgumentException("An installed writer credential is required.");
        }
        if (TimeoutSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(TimeoutSeconds));
        if (string.IsNullOrWhiteSpace(Network)) throw new ArgumentException("Signer network is required.");
    }
}

public sealed record SignerIdentity(string Network, NLightning.Domain.Crypto.ValueObjects.CompactPubKey NodePublicKey,
                                   NLightning.Domain.Bitcoin.ValueObjects.BitcoinKeyPath ChannelKeyPath, uint HeightOfBirth);
public sealed class RemoteSignerTransportException : Exception
{
    private readonly NLightning.Signing.Contracts.SigningRequest? _request;
    /// <summary>Exact failed envelope; persist securely before process exit if its result needs reconciliation.</summary>
    public NLightning.Signing.Contracts.SigningRequest? Request => _request?.Clone();
    public RemoteSignerTransportException(string message, Exception? inner = null,
                                         NLightning.Signing.Contracts.SigningRequest? request = null) : base(message, inner)
        => _request = request?.Clone();
}