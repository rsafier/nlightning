namespace NLightning.Domain.Bitcoin.SilentPayments.Interfaces;

using Crypto.ValueObjects;

/// <summary>Receiver operations that keep the BIP 352 scan private key inside its secure owner.</summary>
public interface ISilentPaymentKeySource
{
    CompactPubKey ScanPubKey { get; }
    CompactPubKey SpendPubKey { get; }

    /// <summary>Whether a standard BIP39/BIP32 wallet can restore these keys from the seed.</summary>
    bool RecoverableElsewhere { get; }

    /// <summary>Writes the compressed, unhashed point b_scan * tweakedInputKey to a 33-byte destination.</summary>
    void ComputeScanSharedSecret(ReadOnlySpan<byte> tweakedInputKey, Span<byte> point33);

    /// <summary>Writes hash_BIP0352/Label(ser256(b_scan) || ser32(label)) to a 32-byte destination.</summary>
    void GetLabelTweak(uint label, Span<byte> scalar32);

    /// <summary>Returns the compressed point of the label tweak multiplied by G.</summary>
    CompactPubKey GetLabelPoint(uint label);
}