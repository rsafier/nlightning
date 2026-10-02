using Microsoft.Extensions.Logging;

namespace NLightning.Application.Accounting.Reports.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Interfaces;

/// <summary>
/// What every report and export of the financial book does first (NL-602 A3-T6): refuse when the books are off or the
/// financial book is, then seal, project the operational book and the financial one.
/// </summary>
internal sealed class AccountingFinancialAccess
{
    private readonly AccountingBooksAccess _access;
    private readonly IAccountingBooks? _books;
    private readonly IAccountingFinancialProjection? _projection;

    public AccountingFinancialAccess(IAccountingBooks? books, IAccountingEventSealer? sealer,
                                     IAccountingFinancialProjection? projection, ILogger logger)
    {
        _books = books;
        _projection = projection;
        _access = new AccountingBooksAccess(books, sealer, logger);
    }

    /// <exception cref="AccountingBooksDisabledException">The books are off or not registered.</exception>
    /// <exception cref="AccountingFinancialBooksDisabledException">The financial book is off.</exception>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_books is not { IsEnabled: true })
            throw new AccountingBooksDisabledException();
        if (_projection is { IsEnabled: false })
            throw new AccountingFinancialBooksDisabledException();

        await _access.PrepareAsync(cancellationToken);
        if (_projection is not null)
            await _projection.ProjectNowAsync(cancellationToken);
    }
}