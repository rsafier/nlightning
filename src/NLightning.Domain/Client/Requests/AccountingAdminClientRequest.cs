namespace NLightning.Domain.Client.Requests;

using Enums;

/// <summary>
/// Asks for an administration action of the books (<c>ClientCommand.AccountingAdmin</c>, NL-602 A2).
/// </summary>
public sealed class AccountingAdminClientRequest
{
    public AccountingAdminAction Action { get; init; } = AccountingAdminAction.Verify;
}