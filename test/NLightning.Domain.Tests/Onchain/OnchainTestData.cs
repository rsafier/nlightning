using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Models;
using Domain.Protocol.Models;

/// <summary>
/// BOLT 3 Appendix C channel data for the on-chain tests: the payment basepoints give the obscuring factor
/// <c>0x2bb038521914</c>, and commitment 42 is every Appendix C commitment's number.
/// </summary>
internal static class OnchainTestData
{
    public const ulong AppendixCObscuringFactor = 0x2bb038521914;
    public const ulong AppendixCCommitmentNumber = 42;

    /// <summary>Appendix C commit tx locktime (<c>3e195220</c> little-endian in the tx).</summary>
    public const uint AppendixCLockTime = 0x2052193e;

    /// <summary>Appendix C commit tx funding input sequence (<c>38b02b80</c> little-endian in the tx).</summary>
    public const uint AppendixCSequence = 0x802bb038;

    public static readonly TxId FundingTxId =
        Convert.FromHexString("8984484a580b825b9972d7adb15050b3ab624ccd731946b3eeddb92f4e7ef6be");

    public static CommitmentNumber AppendixCCommitmentNumberHelper() =>
        new(Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa"),
            Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991"),
            new FakeSha256());

    public static TxId TxIdOf(byte seed)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, seed);
        return bytes;
    }

    /// <summary>A one-input funding spend with the commitment form of <paramref name="number"/>.</summary>
    public static ChainTx CommitmentSpend(CommitmentNumber helper, ulong number, TxId txId,
                                          params ChainTxOutput[] outputs) =>
        new(txId, 2, helper.LockTime(number),
            [new ChainTxInput(FundingTxId, 0, helper.Sequence(number), [])], outputs);

    /// <summary>A legacy closing transaction shape: locktime 0, sequence 0xFFFFFFFF.</summary>
    public static ChainTx ClosingSpend(TxId txId, params ChainTxOutput[] outputs) =>
        new(txId, 2, 0, [new ChainTxInput(FundingTxId, 0, 0xFFFFFFFF, [])], outputs);
}