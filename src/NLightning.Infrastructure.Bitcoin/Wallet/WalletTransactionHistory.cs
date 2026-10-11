using System.Globalization;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;

/// <summary>Raw history and its lightweight, ownership-preserving query projection.</summary>
public static class WalletTransactionHistory
{
    public static WalletTransactionRecord Describe(Transaction transaction, uint? height, byte[]? blockHash,
        DateTimeOffset timestamp, IReadOnlyList<uint> outputs, IReadOnlyList<WalletTransactionInput> inputs)
    {
        var parts = new List<string>();
        foreach (var index in outputs)
        {
            var output = transaction.Outputs[checked((int)index)];
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"o:{index}:{output.Value.Satoshi}:{Convert.ToHexString(output.ScriptPubKey.ToBytes())}"));
        }
        foreach (var input in inputs)
        {
            var point = transaction.Inputs[checked((int)input.InputIndex)].PrevOut;
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"i:{input.InputIndex}:{input.AmountSat}:{Convert.ToHexString(point.Hash.ToBytes())}:{point.N}"));
        }
        return new WalletTransactionRecord(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes(), height,
            blockHash, timestamp, outputs, inputs, string.Join(';', parts));
    }
}