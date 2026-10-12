namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// The books are off (<c>Accounting:Enabled=false</c>) or not available on this node: there is nothing to report.
/// </summary>
public sealed class AccountingBooksDisabledException : InvalidOperationException
{
    public AccountingBooksDisabledException()
        : base("The accounting books are disabled (Accounting:Enabled=false); the feed is still recorded and "
             + "listaccountingevents shows it.")
    {
    }
}