namespace NLightning.Infrastructure.RemoteSigning;

public sealed class RemoteSignerOptions
{
    public string SocketPath { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string Network { get; set; } = "Regtest";
    public string? ExpectedNodePublicKey { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SocketPath) || !Path.IsPathFullyQualified(SocketPath))
            throw new ArgumentException("Signer SocketPath must be absolute.");
        if (AuthToken.Length < 32 || AuthToken.Any(c => c is < (char)33 or > (char)126)) throw new ArgumentException("Signer authentication token must contain at least 32 printable ASCII characters without spaces.");
        if (TimeoutSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(TimeoutSeconds));
        if (string.IsNullOrWhiteSpace(Network)) throw new ArgumentException("Signer network is required.");
    }
}

public sealed record SignerIdentity(string Network, NLightning.Domain.Crypto.ValueObjects.CompactPubKey NodePublicKey,
                                   NLightning.Domain.Bitcoin.ValueObjects.BitcoinKeyPath ChannelKeyPath, uint HeightOfBirth);
public sealed class RemoteSignerTransportException(string message, Exception? inner = null) : Exception(message, inner);