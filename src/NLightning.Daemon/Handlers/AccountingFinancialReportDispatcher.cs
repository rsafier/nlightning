namespace NLightning.Daemon.Handlers;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Financial.Reports;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;

/// <summary>
/// The financial book's side of <c>accounting report</c> (ClientCommand 43) and <c>accounting export</c> (44), NL-602
/// A3-T6: <c>--book financial</c>, or a kind only the financial book has (realized and unrealized gains, lots, unvalued,
/// unclassified) or the risk-weighted capital.
/// </summary>
/// <remarks>
/// The books or the financial book off, a bad currency, price, page or period, or no stored price where one is needed
/// is <c>invalid_operation</c> with the reason. The operational-only views (channels, peers, fees) are refused with
/// <c>--book financial</c>.
/// </remarks>
internal static class AccountingFinancialReportDispatcher
{
    /// <summary>Whether a report request goes to the financial reports.</summary>
    public static bool IsFinancial(AccountingReportClientRequest request) =>
        request.Book == AccountingBook.Financial || request.Kind >= AccountingReportKind.RealizedGains;

    public static async Task<AccountingReportClientResponse> HandleAsync(
        IAccountingFinancialReports? reports, AccountingReportClientRequest request, AccountNames names,
        ChannelId? channelId, AccountRole? account, CancellationToken ct)
    {
        var financial = reports ?? throw AccountingReportClientHandler.BooksDisabled();
        var response = new AccountingReportClientResponse(request.Kind, names);
        try
        {
            var currency = AccountingFiat.NormalizeCurrency(request.Currency);
            var result = new AccountingFinancialReportResult(currency);
            switch (request.Kind)
            {
                case AccountingReportKind.BalanceSheet:
                    result = result with
                    {
                        BalanceSheet = await financial.GetBalanceSheetAsync(request.Until, currency, request.Price,
                                                                            request.Price is not null, ct)
                    };
                    break;
                case AccountingReportKind.IncomeStatement:
                    result = result with
                    {
                        IncomeStatement = await financial.GetIncomeStatementAsync(request.Since, request.Until,
                                                                                  currency, ct)
                    };
                    break;
                case AccountingReportKind.RealizedGains:
                    result = result with
                    {
                        RealizedGains = await financial.GetRealizedGainsAsync(request.Since, request.Until,
                                                                              request.Grouping, currency, ct)
                    };
                    break;
                case AccountingReportKind.Lots:
                case AccountingReportKind.UnrealizedGains:
                    ThrowIfNegativeCursor(request.AfterLedgerSeq);
                    ClientRequestGuards.ThrowIfInvalidPage(0, request.Take);
                    result = result with
                    {
                        Lots = await financial.GetLotsAsync(request.AfterLedgerSeq, request.Take, currency,
                                                            request.Price,
                                                            request.Kind == AccountingReportKind.UnrealizedGains, ct)
                    };
                    break;
                case AccountingReportKind.Register:
                case AccountingReportKind.Unclassified:
                    ThrowIfNegativeCursor(request.AfterLedgerSeq);
                    ClientRequestGuards.ThrowIfInvalidPage(0, request.Take);
                    result = result with
                    {
                        Register = await financial.GetRegisterAsync(
                                       new AccountingEntryQuery(request.AfterLedgerSeq, request.Take, request.Since,
                                                                request.Until, request.EventKinds, channelId, account)
                                       {
                                           Book = AccountingBook.Financial,
                                           AfterAdjustment = request.AfterAdjustment ?? int.MaxValue,
                                           WithFlags = request.Kind == AccountingReportKind.Unclassified
                                                           ? AccountingEntryFlags.Unclassified
                                                           : AccountingEntryFlags.None
                                       }, ct)
                    };
                    break;
                case AccountingReportKind.Unvalued:
                    ClientRequestGuards.ThrowIfInvalidPage(0, request.Take);
                    result = result with { Unvalued = await financial.GetUnvaluedAsync(request.Take, ct) };
                    break;
                case AccountingReportKind.RiskCapital:
                    result = result with
                    {
                        RiskCapital = await financial.GetRiskCapitalAsync(currency, request.Price, ct)
                    };
                    break;
                default:
                    throw new ClientException(ErrorCodes.InvalidOperation,
                                              $"The {request.Kind} report is not available for the financial book.");
            }

            return response with { Financial = result };
        }
        catch (AccountingBooksDisabledException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (AccountingFinancialBooksDisabledException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }

    public static async Task<AccountingExportClientResponse> ExportAsync(IAccountingFinancialExports? exports,
                                                                         AccountingExportClientRequest request,
                                                                         CancellationToken ct)
    {
        var financial = exports ?? throw AccountingReportClientHandler.BooksDisabled();
        ThrowIfNegativeCursor(request.AfterLedgerSeq);
        if (request.AfterAdjustment is < 0)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The cursor's adjustment must not be negative (was {request.AfterAdjustment}).");
        ClientRequestGuards.ThrowIfInvalidPage(0, request.Take);
        if (request.Since is { } since && request.Until is { } until && until <= since)
            throw new ClientException(ErrorCodes.InvalidOperation, "until must be after since.");

        try
        {
            var chunk = await financial.ExportAsync(
                            new AccountingFinancialExportQuery(request.Format, request.AfterLedgerSeq, request.Take,
                                                               request.Since, request.Until, request.Currency,
                                                               request.AfterAdjustment), ct);
            return new AccountingExportClientResponse(
                request.Format, new AccountingExportChunk(chunk.Text, chunk.NextAfter, chunk.HasMore, chunk.EntryCount))
            {
                NextAfterAdjustment = chunk.NextAfterAdjustment
            };
        }
        catch (AccountingBooksDisabledException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (AccountingFinancialBooksDisabledException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }

    private static void ThrowIfNegativeCursor(long after)
    {
        if (after < 0)
            throw new ClientException(ErrorCodes.InvalidOperation, $"after must not be negative (was {after}).");
    }
}