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
    Verify = 3
}