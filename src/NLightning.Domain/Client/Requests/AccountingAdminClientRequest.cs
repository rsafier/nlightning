namespace NLightning.Domain.Client.Requests;

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
}