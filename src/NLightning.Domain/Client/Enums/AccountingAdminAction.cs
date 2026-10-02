namespace NLightning.Domain.Client.Enums;

/// <summary>
/// What <c>accounting</c> administration does (<c>ClientCommand.AccountingAdmin</c>, NL-602 A2). The values go on the
/// wire: never renumber them.
/// </summary>
public enum AccountingAdminAction
{
    /// <summary>Compare the books with a live snapshot of the node.</summary>
    Reconcile = 1,

    /// <summary>Clear the books and project the whole feed again.</summary>
    Rebuild = 2,

    /// <summary>Walk the feed's hash chain from the first sealed event.</summary>
    Verify = 3,

    // 4-9 are left to the period close and classification commands (A3-T3, A3-T5)

    /// <summary>Store the operator's prices (<c>accounting prices import</c>, NL-602 A3-T2).</summary>
    PricesImport = 10,

    /// <summary>List the stored prices (<c>accounting prices list</c>, NL-602 A3-T2).</summary>
    PricesList = 11,

    /// <summary>Ask the price sources for a range of hours (<c>accounting prices fetch</c>, NL-602 A3-T2).</summary>
    PricesFetch = 12
}