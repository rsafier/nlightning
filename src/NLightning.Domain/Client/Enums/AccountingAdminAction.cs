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

    /// <summary>Walk the feed's hash chain from the first sealed event (and, since A3-T5, check every period
    /// close).</summary>
    Verify = 3,

    /// <summary>The financial book's classification: rules, overrides, a test and the unclassified listing
    /// (<see cref="AccountingClassifyAction"/>, NL-602 A3-T3).</summary>
    Classify = 5,

    /// <summary>Store the operator's prices (<c>accounting prices import</c>, NL-602 A3-T2).</summary>
    PricesImport = 10,

    /// <summary>List the stored prices (<c>accounting prices list</c>, NL-602 A3-T2).</summary>
    PricesList = 11,

    /// <summary>Ask the price sources for a range of hours (<c>accounting prices fetch</c>, NL-602 A3-T2).</summary>
    PricesFetch = 12,

    // A3-T5 (period close) takes 20-22 so the parallel A3 lanes never collide on a value

    /// <summary>Close a period of the financial book (<c>accounting close &lt;period&gt; [--force]</c>).</summary>
    Close = 20,

    /// <summary>List the periods (<c>accounting close list</c>).</summary>
    CloseList = 21,

    /// <summary>Show one period's close (<c>accounting close show &lt;period&gt;</c>).</summary>
    CloseShow = 22
}