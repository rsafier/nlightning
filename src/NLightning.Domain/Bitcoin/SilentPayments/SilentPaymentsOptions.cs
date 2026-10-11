namespace NLightning.Domain.Bitcoin.SilentPayments;

using Enums;

/// <summary>Opt-in BIP 352 wallet sending and full-node receipt scanning. Bindable without runtime reflection.</summary>
public sealed class SilentPaymentsOptions
{
    public const string SectionName = "SilentPayments";

    public bool Enabled { get; set; }
    public bool Send { get; set; } = true;
    public bool Receive { get; set; } = true;
    public bool AllowMainnet { get; set; }
    public bool AvoidMixing { get; set; } = true;
    public bool ChangeToSilentPayment { get; set; }
    public long MinSendSat { get; set; } = 546;
    public long MinReceiveSat { get; set; } = 1_000;
    public int MaxLabels { get; set; } = 1_000;
    public int RecoveryLabelCount { get; set; } = 100;
    public uint? BirthdayHeight { get; set; }
    public int RescanBlocksPerSecond { get; set; }
    public SilentPaymentPrevoutSource PrevoutSource { get; set; } = SilentPaymentPrevoutSource.Auto;

    public IReadOnlyList<string> GetValidationErrors(bool isMainnet = false)
    {
        var errors = new List<string>();
        if (Enabled && isMainnet && !AllowMainnet)
            errors.Add($"{SectionName}: receiving or sending on mainnet requires AllowMainnet=true.");
        if (MinSendSat < 0 || MinReceiveSat < 0)
            errors.Add($"{SectionName}: minimum amounts cannot be negative.");
        if (MaxLabels is < 1 or > 100_000 || RecoveryLabelCount is < 0 or > 100_000)
            errors.Add($"{SectionName}: MaxLabels must be 1..100000 and RecoveryLabelCount 0..100000.");
        if (RescanBlocksPerSecond < 0)
            errors.Add($"{SectionName}: RescanBlocksPerSecond cannot be negative.");
        if (!Enum.IsDefined(PrevoutSource))
            errors.Add($"{SectionName}: unknown prevout source.");
        return errors;
    }
}