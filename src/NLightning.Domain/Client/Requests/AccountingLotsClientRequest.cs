namespace NLightning.Domain.Client.Requests;

using Accounting.Financial.Lots;

/// <summary>
/// The arguments of <c>accounting lots import &lt;csv&gt;</c> (<c>ClientCommand.AccountingAdmin</c>, D-A9; NL-602
/// A3-T4).
/// </summary>
public sealed class AccountingLotsClientRequest
{
    /// <summary>The lots' currency, or null for the financial book's.</summary>
    public string? Currency { get; init; }

    /// <summary>The lots, in file order.</summary>
    public IReadOnlyList<AccountingLotPoint> Lots { get; init; } = [];
}