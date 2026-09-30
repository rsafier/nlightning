using System.Security.Cryptography;

namespace NLightning.Domain.Tests.Protocol.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>Builders shared by the interactive-tx tests.</summary>
internal static class InteractiveTxTestData
{
    public static readonly ChannelId TestChannelId = new(Enumerable.Repeat((byte)0xAB, 32).ToArray());

    public static readonly CompactPubKey LowNodeId = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    public static readonly CompactPubKey HighNodeId = new([0x03, .. Enumerable.Repeat((byte)0x22, 32)]);

    public static readonly BitcoinScript P2Wpkh = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x33, 20)]);
    public static readonly BitcoinScript P2Wsh = new([0x00, 0x20, .. Enumerable.Repeat((byte)0x44, 32)]);
    public static readonly BitcoinScript P2Tr = new([0x51, 0x20, .. Enumerable.Repeat((byte)0x55, 32)]);
    public static readonly BitcoinScript FundingScript = new([0x00, 0x20, .. Enumerable.Repeat((byte)0x66, 32)]);

    public static readonly TxId FundingTxId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());

    /// <summary>The amount every prevtx output of <see cref="FakePrevTxInspector"/> holds.</summary>
    public static readonly LightningMoney PrevOutAmount = LightningMoney.Satoshis(100_000);

    public const uint Sequence = 0xFFFFFFFD;

    /// <summary>A distinct fake prevtx; its txid is its SHA-256.</summary>
    public static byte[] PrevTx(int seed) => [0x02, .. BitConverter.GetBytes(seed)];

    public static TxId PrevTxId(byte[] prevTx) => new(SHA256.HashData(prevTx));

    public static InteractiveTxSessionParameters Parameters(bool isInitiator,
                                                            InteractiveTxContribution? contribution = null,
                                                            SharedFundingSpec? shared = null,
                                                            CompactPubKey? localNodeId = null,
                                                            CompactPubKey? remoteNodeId = null,
                                                            uint feeratePerKw = 253,
                                                            ulong dustLimitSatoshis = 0,
                                                            IReadOnlyList<ConstructedInteractiveTx>? previousAttempts =
                                                                null) =>
        new(TestChannelId, isInitiator, feeratePerKw, 120, dustLimitSatoshis,
            contribution ?? InteractiveTxContribution.Empty, shared,
            false, false, localNodeId ?? LowNodeId, remoteNodeId ?? HighNodeId, previousAttempts ?? []);

    public static ContributedInput Input(int seed, long sats = 100_000, uint sequence = Sequence) =>
        new(PrevTxId(PrevTx(seed)), 0, PrevTx(seed), sequence, LightningMoney.Satoshis(sats), P2Wpkh, 272);

    public static ContributedOutput Output(long sats, BitcoinScript? script = null) =>
        new(LightningMoney.Satoshis(sats), script ?? P2Wpkh, true);

    public static InteractiveTxContribution Contribution(IReadOnlyList<ContributedInput>? inputs = null,
                                                         IReadOnlyList<ContributedOutput>? outputs = null) =>
        new(inputs ?? [], outputs ?? [], null);

    /// <summary>A splice spec: 1,000,000 sat funding, balances 600,000 (initiator) / 400,000.</summary>
    public static SharedFundingSpec Splice(bool localIsInitiator, long newCapacity = 1_000_000,
                                           long localOut = 600_000, long remoteOut = 400_000)
    {
        var input = new SharedFundingInput(FundingTxId, 1, LightningMoney.Satoshis(1_000_000), FundingScript, 384);
        return new SharedFundingSpec(input, FundingScript, LightningMoney.Satoshis(newCapacity),
                                     LightningMoney.Satoshis(localIsInitiator ? 600_000 : 400_000),
                                     LightningMoney.Satoshis(localIsInitiator ? 400_000 : 600_000),
                                     LightningMoney.Satoshis(localOut), LightningMoney.Satoshis(remoteOut));
    }

    public static TxAddInputMessage AddInput(ulong serialId, int seed, uint vout = 0, uint sequence = Sequence) =>
        new(new TxAddInputPayload(TestChannelId, serialId, PrevTx(seed), vout, sequence));

    public static TxAddInputMessage AddSharedInput(ulong serialId, TxId? txId = null, uint vout = 1) =>
        new(new TxAddInputPayload(TestChannelId, serialId, [], vout, Sequence),
            new SharedInputTxIdTlv(txId ?? FundingTxId));

    public static TxAddOutputMessage AddOutput(ulong serialId, long sats = 50_000, BitcoinScript? script = null) =>
        new(new TxAddOutputPayload(LightningMoney.Satoshis(sats), TestChannelId, script ?? P2Wpkh, serialId));

    public static TxRemoveInputMessage RemoveInput(ulong serialId) => new(new TxRemoveInputPayload(TestChannelId, serialId));

    public static TxRemoveOutputMessage RemoveOutput(ulong serialId) =>
        new(new TxRemoveOutputPayload(TestChannelId, serialId));

    public static TxCompleteMessage Complete() => new(new TxCompletePayload(TestChannelId));

    public static TxAbortMessage AbortMessage(byte[] data) => new(new TxAbortPayload(TestChannelId, data));

    public static TxSignaturesMessage Signatures(TxId txId, IEnumerable<Witness> witnesses,
                                                 CompactSignature? sharedSignature = null) =>
        new(new TxSignaturesPayload(TestChannelId, [.. (byte[])txId], [.. witnesses]),
            sharedSignature is null ? null : new SharedInputSignatureTlv(sharedSignature));

    /// <summary>A DER signature (r = s = 1) followed by <paramref name="sighash"/>.</summary>
    public static byte[] DerSignature(byte sighash = 0x01) => [0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, sighash];

    /// <summary>A BIP 141 witness stack serialization.</summary>
    public static Witness WitnessOf(params byte[][] items)
    {
        var bytes = new List<byte> { (byte)items.Length };
        foreach (var item in items)
        {
            bytes.Add((byte)item.Length);
            bytes.AddRange(item);
        }

        return new Witness([.. bytes]);
    }

    public static Witness P2WpkhWitness(byte sighash = 0x01) =>
        WitnessOf(DerSignature(sighash), [0x02, .. Enumerable.Repeat((byte)0x01, 32)]);

    public static CompactSignature SharedSignature() => new(Enumerable.Repeat((byte)0x01, 64).ToArray());

    /// <summary>Builds the transaction as <c>IInteractiveTxBuilder.Build</c> would (inputs and outputs by serial id).</summary>
    public static ConstructedInteractiveTx Construct(InteractiveTxSession session, uint? locktime = null) =>
        new(new TxId(SHA256.HashData(BitConverter.GetBytes(session.Inputs.Count))), [0x02], locktime ?? 120,
            [.. session.Inputs.OrderBy(i => i.SerialId)], [.. session.Outputs.OrderBy(o => o.SerialId)], 1_000, null);
}

/// <summary>
/// Reads fake prevtxs: every prevtx is valid, its txid is its SHA-256 and it has two outputs of
/// <see cref="InteractiveTxTestData.PrevOutAmount"/> paying <see cref="InteractiveTxTestData.P2Wpkh"/>, unless
/// <see cref="Override"/> says otherwise.
/// </summary>
internal sealed class FakePrevTxInspector : IPrevTxInspector
{
    public Func<byte[], uint, PrevTxInspection?>? Override { get; init; }

    public int Calls { get; private set; }

    public PrevTxInspection Inspect(ReadOnlyMemory<byte> prevTx, uint prevTxVout)
    {
        Calls++;
        var bytes = prevTx.ToArray();
        var overridden = Override?.Invoke(bytes, prevTxVout);
        if (overridden is not null)
            return overridden;

        var txId = InteractiveTxTestData.PrevTxId(bytes);
        return prevTxVout < 2
                   ? new PrevTxInspection(true, txId, 2, InteractiveTxTestData.PrevOutAmount,
                                          InteractiveTxTestData.P2Wpkh, true, null)
                   : new PrevTxInspection(false, txId, 2, null, null, false, "prevtx_vout out of range");
    }

    public Task<bool> IsConfirmedAsync(TxId txId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}