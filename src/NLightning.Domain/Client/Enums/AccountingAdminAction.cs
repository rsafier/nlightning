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

    // A3-T5 (period close) takes 20-22 so the parallel A3 lanes never collide on a value

    /// <summary>Close a period of the financial book (<c>accounting close &lt;period&gt; [--force]</c>).</summary>
    Close = 20,

    /// <summary>List the periods (<c>accounting close list</c>).</summary>
    CloseList = 21,

    /// <summary>Show one period's close (<c>accounting close show &lt;period&gt;</c>).</summary>
    CloseShow = 22
}