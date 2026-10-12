namespace NLightning.Domain.Signing;

using Crypto.ValueObjects;
using Protocol.ValueObjects;

/// <summary>Immutable enrolled authority for one logical node and its funds.</summary>
public sealed record NodeSigningContext
{
    public const string DefaultNodeId = "default-node";
    public const string DefaultOwnerId = "default-owner";
    public const string DefaultSignerId = "default-signer";

    private readonly string _nodePublicKey;
    public string NodeId { get; init; }
    public string OwnerId { get; init; }
    public string SignerId { get; init; }
    public string Network { get; init; }
    public CompactPubKey NodePublicKey => new(Convert.FromHexString(_nodePublicKey));

    public NodeSigningContext(string nodeId, string ownerId, string signerId, string network, CompactPubKey nodePublicKey)
    {
        NodeId = nodeId;
        OwnerId = ownerId;
        SignerId = signerId;
        Network = BitcoinNetwork.Resolve(network).Name;
        if (((ReadOnlySpan<byte>)nodePublicKey).Length != 33)
            throw new ArgumentException("The enrolled Lightning identity must be a compressed public key.");
        _nodePublicKey = nodePublicKey.ToString();
    }

    public void Validate()
    {
        ValidateIdentifier(NodeId, nameof(NodeId));
        ValidateIdentifier(OwnerId, nameof(OwnerId));
        ValidateIdentifier(SignerId, nameof(SignerId));
        _ = BitcoinNetwork.Resolve(Network);
        if (((ReadOnlySpan<byte>)NodePublicKey).Length != 33)
            throw new ArgumentException("The enrolled Lightning identity must be a compressed public key.");
    }

    public static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Context identifiers require 1 to 128 printable ASCII characters without spaces.", name);
    }
}