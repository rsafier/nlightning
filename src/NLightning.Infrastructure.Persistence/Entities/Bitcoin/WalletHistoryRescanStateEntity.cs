namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

/// <summary>The singleton restart-safe history job, independent of the live blockchain cursor.</summary>
public sealed class WalletHistoryRescanStateEntity
{
    public int Id { get; set; } = 1;
    public Guid Generation { get; set; }
    public uint RequestedFromHeight { get; set; }
    public uint AvailableFromHeight { get; set; }
    public uint TargetHeight { get; set; }
    public uint? CursorHeight { get; set; }
    public byte[]? CursorHash { get; set; }
    public uint AddressCount { get; set; }
    public bool IsActive { get; set; }
    public bool IsPartial { get; set; }
    public string? Error { get; set; }
}