namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Crypto.ValueObjects;

public sealed record SilentPaymentScanState(uint BirthdayHeight, uint LiveFromHeight,
    uint? RescanCursorHeight = null, Hash? RescanCursorHash = null, uint? RescanTargetHeight = null,
    string? PrevoutSource = null, uint? LiveCursorHeight = null, Hash? LiveCursorHash = null,
    uint RecoveryLabelCount = 0);