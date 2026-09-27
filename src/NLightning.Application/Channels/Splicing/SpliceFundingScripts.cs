namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Infrastructure.Bitcoin.Builders;

/// <summary>
/// The Bitcoin shapes of a splice's shared funding (BOLT 3 "Funding Transaction Output"): the P2WSH 2-of-2 output of a
/// pair of funding keys, and the witness that spends it (<c>0 &lt;sig1&gt; &lt;sig2&gt; &lt;2 key1 key2 2
/// CHECKMULTISIG&gt;</c>, the keys and signatures in the lexicographic order of the keys).
/// </summary>
public static class SpliceFundingScripts
{
    /// <summary>
    /// The witness weight of a 2-of-2 funding input: item count (1), the empty item (1), two DER signatures with their
    /// sighash byte at most (1 + 73 each) and the 71-byte witness script (1 + 71).
    /// </summary>
    public const int SharedInputWitnessWeight = 1 + 1 + 1 + 73 + 1 + 73 + 1 + 71;

    /// <summary>The weight of the signed shared input a splice initiator pays for (IT-S-03, SP-TX-03).</summary>
    public const int SharedInputWeight = CollaborativeFeeCalculator.InputBaseWeight + SharedInputWitnessWeight;

    private const byte SighashAll = 0x01;

    /// <summary>The P2WSH scriptPubKey and the witness script of the funding output of two funding keys.</summary>
    /// <exception cref="ArgumentException">Both keys are the same.</exception>
    public static (BitcoinScript ScriptPubKey, BitcoinScript WitnessScript) Create(CompactPubKey localFundingPubKey,
                                                                                  CompactPubKey remoteFundingPubKey)
    {
        // The amount does not change the scripts; the builder refuses zero
        var output = new FundingOutputBuilder().Build(new FundingOutputInfo(LightningMoney.Satoshis(1),
                                                                            localFundingPubKey,
                                                                            remoteFundingPubKey));
        return (output.BitcoinScriptPubKey, output.RedeemBitcoinScript);
    }

    /// <summary>
    /// The serialized witness of the 2-of-2 funding input (BOLT 3), from both 64-byte compact signatures
    /// (<c>SIGHASH_ALL</c>), in the order of the keys.
    /// </summary>
    public static Witness BuildWitness(CompactPubKey localFundingPubKey, CompactPubKey remoteFundingPubKey,
                                       CompactSignature localSignature, CompactSignature remoteSignature)
    {
        var (_, witnessScript) = Create(localFundingPubKey, remoteFundingPubKey);
        var localFirst = ((ReadOnlySpan<byte>)(byte[])localFundingPubKey).SequenceCompareTo(
                             (byte[])remoteFundingPubKey) < 0;
        byte[] first = [.. ToDer(localFirst ? localSignature : remoteSignature), SighashAll];
        byte[] second = [.. ToDer(localFirst ? remoteSignature : localSignature), SighashAll];

        using var stream = new MemoryStream();
        WriteCompactSize(stream, 4);
        WriteItem(stream, []);
        WriteItem(stream, first);
        WriteItem(stream, second);
        WriteItem(stream, witnessScript);
        return new Witness(stream.ToArray());
    }

    /// <summary>The strict DER encoding (BIP 66) of a 64-byte compact <c>r || s</c> signature.</summary>
    public static byte[] ToDer(CompactSignature signature)
    {
        byte[] compact = signature;
        if (compact.Length != 64)
            throw new ArgumentException("A compact signature is 64 bytes", nameof(signature));

        var r = ToDerInteger(compact.AsSpan(0, 32));
        var s = ToDerInteger(compact.AsSpan(32, 32));
        return [0x30, (byte)(2 + r.Length + 2 + s.Length), 0x02, (byte)r.Length, .. r, 0x02, (byte)s.Length, .. s];
    }

    private static byte[] ToDerInteger(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length - 1 && value[start] == 0 && value[start + 1] < 0x80)
            start++;

        var trimmed = value[start..];
        return trimmed[0] >= 0x80 ? [0x00, .. trimmed] : trimmed.ToArray();
    }

    private static void WriteItem(Stream stream, byte[] item)
    {
        WriteCompactSize(stream, (ulong)item.Length);
        stream.Write(item);
    }

    private static void WriteCompactSize(Stream stream, ulong value)
    {
        switch (value)
        {
            case < 0xFD:
                stream.WriteByte((byte)value);
                break;
            case <= 0xFFFF:
                stream.WriteByte(0xFD);
                stream.WriteByte((byte)value);
                stream.WriteByte((byte)(value >> 8));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), "A witness item is below 65,536 bytes");
        }
    }
}