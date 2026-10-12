namespace NLightning.Domain.Bitcoin.Wallet.Models;

/// <summary>A bounded history-only job; each block's records and cursor commit together.</summary>
public sealed record WalletHistoryRescanState(Guid Generation, uint RequestedFromHeight, uint AvailableFromHeight,
    uint TargetHeight, uint? CursorHeight, byte[]? CursorHash, uint AddressCount, bool IsActive,
    bool IsPartial, string? Error = null);