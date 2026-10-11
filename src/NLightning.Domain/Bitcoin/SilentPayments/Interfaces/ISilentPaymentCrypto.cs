namespace NLightning.Domain.Bitcoin.SilentPayments.Interfaces;

using Crypto.ValueObjects;
using Models;

public interface ISilentPaymentCrypto
{
    bool IsValidPoint(CompactPubKey key);
    bool TryGetInputPublicKey(ReadOnlySpan<byte> prevoutScript, ReadOnlySpan<byte> scriptSig,
                             IReadOnlyList<byte[]> witness, out CompactPubKey key);
    bool TrySumPublicKeys(IReadOnlyList<CompactPubKey> keys, out CompactPubKey sum);
    byte[] ComputeInputHash(ReadOnlySpan<byte> smallestOutpoint36, CompactPubKey sumKey);
    CompactPubKey TweakInputPublicKey(CompactPubKey sumKey, ReadOnlySpan<byte> inputHash32);
    IReadOnlyList<SilentPaymentDerivedOutput> DeriveOutputs(IReadOnlyList<SilentPaymentSenderInput> inputs,
                                                          IReadOnlyList<SilentPaymentRecipient> recipients);
    IReadOnlyList<SilentPaymentScanMatch> Scan(ReadOnlySpan<byte> sharedSecret33, CompactPubKey spendKey,
                                             IReadOnlyList<SilentPaymentScanCandidate> candidates,
                                             IReadOnlyDictionary<uint, CompactPubKey>? labelPoints = null);

    /// <summary>Validates and copies label points once for a block; null requests the uncached compatibility path.</summary>
    ISilentPaymentScanContext? PrepareScanContext(IReadOnlyDictionary<uint, CompactPubKey> labelPoints) => null;

    /// <summary>Scans using a context made by this adapter, without validating every label again per transaction.</summary>
    IReadOnlyList<SilentPaymentScanMatch> ScanPrepared(ReadOnlySpan<byte> sharedSecret33, CompactPubKey spendKey,
        IReadOnlyList<SilentPaymentScanCandidate> candidates, ISilentPaymentScanContext context) =>
        throw new NotSupportedException("This crypto adapter has no prepared label scan context.");
}