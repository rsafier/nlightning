namespace NLightning.Domain.Client.Requests;

using Accounting.Books;
using Enums;

/// <summary>
/// Asks for an administration action of the books (<c>ClientCommand.AccountingAdmin</c>, NL-602 A2).
/// </summary>
public sealed class AccountingAdminClientRequest
{
    public AccountingAdminAction Action { get; init; } = AccountingAdminAction.Verify;

    /// <summary>The classify action (<see cref="AccountingAdminAction.Classify"/>, A3-T3).</summary>
    public AccountingClassifyClientRequest? Classify { get; init; }

    /// <summary>The arguments of the <c>prices</c> actions (NL-602 A3-T2).</summary>
    public AccountingPricesClientRequest? Prices { get; init; }

    /// <summary>The period of <c>close</c> and <c>close show</c> (A3-T5): <c>YYYY-MM</c> or
    /// <c>YYYY-MM-DD..YYYY-MM-DD</c>.</summary>
    public string? Period { get; init; }

    /// <summary><c>close --force</c>: close although the period has unvalued or unclassified rows.</summary>
    public bool Force { get; init; }

    /// <summary>The book of <c>rebuild</c> (<c>--book</c>; the operational one when null).</summary>
    public AccountingBook? Book { get; init; }
}