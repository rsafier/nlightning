namespace NLightning.Domain.Bitcoin.Events;

/// <summary>A wallet transaction observed live. Confirmations are published after the block save;
/// a disconnected transaction is published with zero confirmations after the rewind save.</summary>
public sealed class WalletTransactionEventArgs(
    string rawTransactionHex, long amountSat, long feeSat, uint blockHeight, string blockHash,
    DateTimeOffset timestamp, string label, IReadOnlyList<uint> ourOutputs,
    IReadOnlyList<uint> ourInputs, string txHash, bool isReorg = false,
    IReadOnlyList<long>? ourInputAmounts = null) : EventArgs
{
    /// <summary>Captured from the original parsed transaction before commit; never re-derived by a publisher.</summary>
    public string TxHash { get; } = txHash;
    public bool IsReorg { get; } = isReorg;
    public string RawTransactionHex { get; } = rawTransactionHex;
    public long AmountSat { get; } = amountSat;
    public long FeeSat { get; } = feeSat;
    public uint BlockHeight { get; } = blockHeight;
    public string BlockHash { get; } = blockHash;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string Label { get; } = label;
    public IReadOnlyList<uint> OurOutputs { get; } = Array.AsReadOnly(ourOutputs.ToArray());
    public IReadOnlyList<uint> OurInputs { get; } = Array.AsReadOnly(ourInputs.ToArray());

    /// <summary>The value each of <see cref="OurInputs"/> spends, in the same order, when the describer knew it
    /// (the chain monitor's own descriptions); empty otherwise. The wallet's durable history stores them (NL-1187).
    /// </summary>
    public IReadOnlyList<long> OurInputAmounts { get; } = Array.AsReadOnly((ourInputAmounts ?? []).ToArray());
}