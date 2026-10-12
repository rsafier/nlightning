namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// Why a write became an adjustment in the open period instead of changing a closed one (A3-T5, D-A8). Kept in the
/// adjustment's note; never renumber.
/// </summary>
public enum AccountingAdjustmentReason
{
    /// <summary>A fact dated in a closed period that the financial projector reached after the close (an event sealed
    /// late, a reorg's reversal, a memo event of the backfill): its whole financial entry, dated now.</summary>
    LateFact = 1,

    /// <summary>A manual reclassification (<c>classify set</c>) of an entry of a closed period: the move from the old
    /// account to the new one.</summary>
    Override = 2,

    /// <summary>A classification rule added, changed or removed after the close that would classify an entry of a
    /// closed period differently.</summary>
    RuleChange = 3,

    /// <summary>A price found after the close for a posting of a closed period left unvalued (a forced close): its
    /// fiat value.</summary>
    Price = 4,

    /// <summary>Lots imported after the first close (D-A9): the change of cost basis.</summary>
    LotImport = 5,

    /// <summary>Any other correction of a closed period.</summary>
    Other = 99
}