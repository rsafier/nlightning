namespace NLightning.Domain.Client.Responses;

using Accounting.Books;
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
}