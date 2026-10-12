namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Crypto.ValueObjects;

public class SilentPaymentScanStateEntity
{
    public byte Id { get; set; }
    public uint BirthdayHeight { get; set; }
    public uint LiveFromHeight { get; set; }
    public uint? RescanCursorHeight { get; set; }
    public Hash? RescanCursorHash { get; set; }
    public uint? RescanTargetHeight { get; set; }
    public uint RecoveryLabelCount { get; set; }
    public uint? LiveCursorHeight { get; set; }
    public Hash? LiveCursorHash { get; set; }
    public string? PrevoutSource { get; set; }
    internal SilentPaymentScanStateEntity() { }
}