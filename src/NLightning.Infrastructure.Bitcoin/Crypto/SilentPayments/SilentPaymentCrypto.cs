namespace NLightning.Infrastructure.Bitcoin.Crypto.SilentPayments;

using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Crypto.ValueObjects;

public sealed class SilentPaymentCrypto : ISilentPaymentCrypto
{
    public bool IsValidPoint(CompactPubKey key) => Bip352.IsValidPoint(key);

    public bool TryGetInputPublicKey(ReadOnlySpan<byte> prevoutScript, ReadOnlySpan<byte> scriptSig,
                                     IReadOnlyList<byte[]> witness, out CompactPubKey key) =>
        SilentPaymentInputClassifier.TryGetInputPublicKey(prevoutScript, scriptSig, witness, out key,
            bytes => NBitcoin.Crypto.Hashes.Hash160(bytes).ToBytes(), IsValidPoint);

    public bool TrySumPublicKeys(IReadOnlyList<CompactPubKey> keys, out CompactPubKey sum) =>
        Bip352.TrySumPublicKeys(keys, out sum);

    public byte[] ComputeInputHash(ReadOnlySpan<byte> smallestOutpoint36, CompactPubKey sumKey) =>
        Bip352.ComputeInputHash(smallestOutpoint36, sumKey);

    public CompactPubKey TweakInputPublicKey(CompactPubKey sumKey, ReadOnlySpan<byte> inputHash32) =>
        new(Bip352.TweakInputPublicKey(sumKey, inputHash32));

    public IReadOnlyList<SilentPaymentDerivedOutput> DeriveOutputs(IReadOnlyList<SilentPaymentSenderInput> inputs,
                                                                 IReadOnlyList<SilentPaymentRecipient> recipients) =>
        Bip352.DeriveOutputs(inputs, recipients);

    public IReadOnlyList<SilentPaymentScanMatch> Scan(ReadOnlySpan<byte> sharedSecret33, CompactPubKey spendKey,
        IReadOnlyList<SilentPaymentScanCandidate> candidates, IReadOnlyDictionary<uint, CompactPubKey>? labelPoints = null) =>
        Bip352.Scan(sharedSecret33, spendKey, candidates, labelPoints);

    public ISilentPaymentScanContext PrepareScanContext(IReadOnlyDictionary<uint, CompactPubKey> labelPoints) =>
        new ScanContext(Bip352.PrepareLabelLookup(labelPoints));

    public IReadOnlyList<SilentPaymentScanMatch> ScanPrepared(ReadOnlySpan<byte> sharedSecret33, CompactPubKey spendKey,
        IReadOnlyList<SilentPaymentScanCandidate> candidates, ISilentPaymentScanContext context)
    {
        if (context is not ScanContext prepared)
            throw new ArgumentException("The scan context must be prepared by this crypto adapter.", nameof(context));
        return Bip352.Scan(sharedSecret33, spendKey, candidates, preparedLabels: prepared.Labels);
    }

    private sealed record ScanContext(IReadOnlyDictionary<string, uint> Labels) : ISilentPaymentScanContext;
}