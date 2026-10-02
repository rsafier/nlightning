namespace NLightning.Domain.Client.Responses;

using Accounting.Books;
using Accounting.Financial;
using Accounting.Financial.Lots;
using Accounting.Models;
using Enums;

/// <summary>
/// The answer to an administration action of the books (<c>ClientCommand.AccountingAdmin</c>, NL-602 A2): the field
/// of the action is set.
/// </summary>
/// <param name="Action">The action.</param>
/// <param name="Names">The account names in effect (for the reconcile lines).</param>
public sealed record AccountingAdminClientResponse(AccountingAdminAction Action, AccountNames Names)
{
    public AccountingReconcileResult? Reconcile { get; init; }

    /// <summary>How many entries a rebuild wrote.</summary>
    public int? RebuiltEntries { get; init; }

    public AccountingChainVerification? Verification { get; init; }

    /// <summary>The answer of a classify action (A3-T3).</summary>
    public AccountingClassifyClientResponse? Classify { get; init; }

    /// <summary>The answer of a <c>prices</c> action (NL-602 A3-T2).</summary>
    public AccountingPricesClientResponse? Prices { get; init; }

    /// <summary><c>close list</c>: every period, oldest first (A3-T5).</summary>
    public IReadOnlyList<AccountingCloseReport>? Periods { get; init; }

    /// <summary><c>close</c> and <c>close show</c>: the period (A3-T5).</summary>
    public AccountingCloseReport? Period { get; init; }

    /// <summary><c>verify</c>: every closed period checked (A3-T5); null when the closes are not served.</summary>
    public IReadOnlyList<AccountingCloseVerification>? PeriodVerifications { get; init; }

    /// <summary><c>lots import</c>: what the import did (NL-602 A3-T4).</summary>
    public AccountingLotImportResult? LotImport { get; init; }
}