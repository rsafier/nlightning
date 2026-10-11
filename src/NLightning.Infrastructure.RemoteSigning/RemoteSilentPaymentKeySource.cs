using NLightning.Domain.Bitcoin.SilentPayments.Interfaces;
using NLightning.Domain.Crypto.ValueObjects;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Receiver arithmetic whose scan and spend private keys remain in the native signer.</summary>
public sealed class RemoteSilentPaymentKeySource(RemoteSignerConnection connection) : ISilentPaymentKeySource
{
    public CompactPubKey ScanPubKey => Metadata().ScanPubKey;
    public CompactPubKey SpendPubKey => Metadata().SpendPubKey;
    public bool RecoverableElsewhere => Metadata().RecoverableElsewhere;

    public void ComputeScanSharedSecret(ReadOnlySpan<byte> tweakedInputKey, Span<byte> point33)
    {
        if (point33.Length != 33)
            throw new ArgumentException("The shared point destination must be 33 bytes.", nameof(point33));
        CopyResult(NativeSilentPaymentOperations.ScanSharedPoint, point33, tweakedInputKey.ToArray());
    }

    public void GetLabelTweak(uint label, Span<byte> scalar32)
    {
        if (scalar32.Length != 32)
            throw new ArgumentException("The label destination must be 32 bytes.", nameof(scalar32));
        CopyResult(NativeSilentPaymentOperations.LabelTweak, scalar32, label);
    }

    public CompactPubKey GetLabelPoint(uint label) => SignerWire.Read<CompactPubKey>(
        connection.Invoke(NativeSilentPaymentOperations.LabelPoint, label)[0]);

    private SilentPaymentReceiverMetadata Metadata() => SignerWire.Read<SilentPaymentReceiverMetadata>(
        connection.Invoke(NativeSilentPaymentOperations.Metadata)[0]);

    private void CopyResult(uint operation, Span<byte> destination, params object?[] arguments)
    {
        var result = SignerWire.Read<byte[]>(connection.Invoke(operation, arguments)[0]);
        try
        {
            if (result.Length != destination.Length)
                throw new InvalidDataException("The signer returned an invalid silent payment result length.");
            result.CopyTo(destination);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(result); }
    }
}

public sealed record SilentPaymentReceiverMetadata(CompactPubKey ScanPubKey, CompactPubKey SpendPubKey,
                                                  bool RecoverableElsewhere);