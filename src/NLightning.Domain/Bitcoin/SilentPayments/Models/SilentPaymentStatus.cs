namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

public sealed record SilentPaymentStatus(bool Enabled, bool Send, bool Receive, bool RecoverableElsewhere,
    uint? BirthdayHeight, uint? LiveFromHeight, uint? LiveCursorHeight, uint? RescanCursorHeight,
    uint? RescanTargetHeight, uint RecoveryLabelCount, string? PrevoutSource, int FoundOutputs,
    int IgnoredOutputs, int UnspentOutputs, double? LastScanMilliseconds, string? LastError)
{
    public bool IsRescanning => RescanTargetHeight is not null;

    /// <summary>
    /// The unspent, not ignored outputs (NL-1296), so the operator can name them to <c>withdraw --utxo</c>; empty when
    /// not read.
    /// </summary>
    public IReadOnlyList<SilentPaymentUnspentOutput> Unspent { get; init; } = [];
}

/// <summary>One unspent silent payment output: the outpoint (txid in display order), value, block and label.</summary>
public sealed record SilentPaymentUnspentOutput(string TxId, uint Index, long AmountSats, uint BlockHeight, uint? Label);