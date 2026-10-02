using Microsoft.Extensions.Logging;

namespace NLightning.Application.Accounting.Reports;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Interfaces;

/// <summary>
/// What every report and export does first: refuse when the books are off, seal what was committed (so a report
/// includes the events written a moment ago) and project what is sealed.
/// </summary>
internal sealed class AccountingBooksAccess
{
    private readonly IAccountingBooks? _books;
    private readonly ILogger _logger;
    private readonly IAccountingEventSealer? _sealer;

    public AccountingBooksAccess(IAccountingBooks? books, IAccountingEventSealer? sealer, ILogger logger)
    {
        _books = books;
        _sealer = sealer;
        _logger = logger;
    }

    /// <exception cref="AccountingBooksDisabledException">The books are off or not registered.</exception>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_books is not { IsEnabled: true } books)
            throw new AccountingBooksDisabledException();

        if (_sealer is not null)
        {
            try
            {
                await _sealer.SealNowAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The rows stay unsealed for the background round; the books report what is sealed
                _logger.LogWarning(e, "Could not seal the accounting events before reporting");
            }
        }

        await books.ProjectNowAsync(cancellationToken);
    }
}