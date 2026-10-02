namespace NLightning.Domain.Accounting.Financial;

/// <summary>The state of an accounting period (<c>AccountingPeriods.State</c>, A3-T5). Never renumber.</summary>
public enum AccountingPeriodState : byte
{
    Open = 0,

    /// <summary>Locked: nothing in it changes again; corrections post as adjustments in the open period (D-A8).</summary>
    Closed = 1
}