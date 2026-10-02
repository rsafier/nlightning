namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// One account in a report.
/// </summary>
/// <param name="Account">The account.</param>
/// <param name="Name">Its name in effect (<c>Accounting:AccountNames</c>).</param>
/// <param name="AmountMsat">The amount in msat, in the report's sign convention (see the report).</param>
public sealed record AccountingAccountLine(AccountRole Account, string Name, long AmountMsat)
{
    /// <summary><see cref="AmountMsat"/> in satoshis, exact (three decimals at most).</summary>
    public decimal AmountSat => AmountMsat / 1_000m;
}