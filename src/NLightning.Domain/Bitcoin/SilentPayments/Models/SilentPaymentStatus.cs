namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

public sealed record SilentPaymentStatus(bool Enabled, bool Send, bool Receive, bool RecoverableElsewhere,
    uint? BirthdayHeight, uint? LiveFromHeight, uint? LiveCursorHeight, uint? RescanCursorHeight,
    uint? RescanTargetHeight, uint RecoveryLabelCount, string? PrevoutSource, int FoundOutputs,
    int IgnoredOutputs, int UnspentOutputs, double? LastScanMilliseconds, string? LastError)
{
    public bool IsRescanning => RescanTargetHeight is not null;
}