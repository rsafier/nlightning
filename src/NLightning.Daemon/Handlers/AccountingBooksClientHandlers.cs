using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Accounting;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Prices;
using Domain.Accounting.Services;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// A report of the operational books (ClientCommand 43, <c>accounting report</c>, NL-602 A2).
/// </summary>
/// <remarks>
/// The books off (or not registered) is <c>invalid_operation</c> "books disabled"; a bad filter (an empty period, an
/// unknown account, a page outside 1 to <see cref="ClientRequestGuards.MaxPageSize"/>) is <c>invalid_operation</c>. A
/// <c>short_channel_id</c> filter is resolved through the loaded channels.
/// </remarks>
public sealed class AccountingReportClientHandler
    : IClientCommandHandler<AccountingReportClientRequest, AccountingReportClientResponse>
{
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IAccountingFinancialReports? _financialReports;
    private readonly AccountNames _names;
    private readonly IAccountingReports? _reports;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.AccountingReport;

    public AccountingReportClientHandler(IAccountingReports? reports, IOptions<AccountingOptions>? options = null,
                                         IChannelMemoryRepository? channelMemoryRepository = null,
                                         IAccountingFinancialReports? financialReports = null)
    {
        _reports = reports;
        _names = (options?.Value ?? new AccountingOptions()).GetAccountNames();
        _channelMemoryRepository = channelMemoryRepository;
        _financialReports = financialReports;
    }

    /// <inheritdoc/>
    public async Task<AccountingReportClientResponse> HandleAsync(AccountingReportClientRequest request,
                                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reports = _reports ?? throw BooksDisabled();
        if (request.Since is { } since && request.Until is { } until && until <= since)
            throw new ClientException(ErrorCodes.InvalidOperation, "until must be after since.");

        var channelId = request.ChannelId
                     ?? AccountingChannelFilter.ResolveScid(_channelMemoryRepository, request.ChannelScid);

        // The financial book and the snapshot views (NL-602 A3-T6)
        if (AccountingFinancialReportDispatcher.IsFinancial(request))
            return await AccountingFinancialReportDispatcher.HandleAsync(_financialReports, request, _names, channelId,
                                                                         request.Account is { } name
                                                                             ? ParseAccount(name)
                                                                             : null, ct);

        try
        {
            var response = new AccountingReportClientResponse(request.Kind, _names);
            switch (request.Kind)
            {
                case AccountingReportKind.BalanceSheet:
                    return response with { BalanceSheet = await reports.GetBalanceSheetAsync(request.Until, ct) };
                case AccountingReportKind.IncomeStatement:
                    return response with
                    {
                        IncomeStatement = await reports.GetIncomeStatementAsync(request.Since, request.Until, ct)
                    };
                case AccountingReportKind.Channels:
                case AccountingReportKind.Peers:
                    return response with
                    {
                        Channels = await reports.GetChannelsReportAsync(request.Since, request.Until, channelId, ct)
                    };
                case AccountingReportKind.Fees:
                    return response with { Fees = await reports.GetFeesReportAsync(request.Since, request.Until, ct) };
                case AccountingReportKind.Register:
                    if (request.AfterLedgerSeq < 0)
                        throw new ClientException(ErrorCodes.InvalidOperation,
                                                  $"after must not be negative (was {request.AfterLedgerSeq}).");
                    ClientRequestGuards.ThrowIfInvalidPage(0, request.Take);
                    var account = request.Account is { } text ? ParseAccount(text) : (AccountRole?)null;
                    return response with
                    {
                        Register = await reports.GetRegisterAsync(
                                       new AccountingEntryQuery(request.AfterLedgerSeq, request.Take, request.Since,
                                                                request.Until, request.EventKinds, channelId, account),
                                       ct)
                    };
                default:
                    throw new ClientException(ErrorCodes.InvalidOperation,
                                              $"Unknown accounting report {request.Kind}.");
            }
        }
        catch (AccountingBooksDisabledException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }

    /// <summary>
    /// An account by its role name (<c>Routing</c>, case does not matter) or its name in effect or by default
    /// (<c>income:lightning:routing</c>).
    /// </summary>
    /// <exception cref="ClientException">No account has that name.</exception>
    internal AccountRole ParseAccount(string text)
    {
        if (Enum.TryParse<AccountRole>(text, ignoreCase: true, out var role) && Enum.IsDefined(role)
         && !int.TryParse(text, out _))
            return role;

        foreach (var candidate in Enum.GetValues<AccountRole>())
            if (string.Equals(_names[candidate], text, StringComparison.OrdinalIgnoreCase)
             || string.Equals(AccountNames.Default[candidate], text, StringComparison.OrdinalIgnoreCase))
                return candidate;

        throw new ClientException(ErrorCodes.InvalidOperation,
                                  $"Unknown account '{text}': expected a role such as Routing or an account name such "
                                + $"as {_names[AccountRole.Routing]}.");
    }

    internal static ClientException BooksDisabled() =>
        new(ErrorCodes.InvalidOperation, new AccountingBooksDisabledException().Message);
}

/// <summary>
/// One page of an export of the books (ClientCommand 44, <c>accounting export</c>, NL-602 A2): the text goes back in
/// the response; the client writes it to standard output or a file of its own (the daemon never writes a path).
/// </summary>
public sealed class AccountingExportClientHandler
    : IClientCommandHandler<AccountingExportClientRequest, AccountingExportClientResponse>
{
    private readonly IAccountingExports? _exports;
    private readonly IAccountingFinancialExports? _financialExports;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.AccountingExport;

    public AccountingExportClientHandler(IAccountingExports? exports,
                                         IAccountingFinancialExports? financialExports = null)
    {
        _exports = exports;
        _financialExports = financialExports;
    }

    /// <inheritdoc/>
    public async Task<AccountingExportClientResponse> HandleAsync(AccountingExportClientRequest request,
                                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Book == AccountingBook.Financial)
            return await AccountingFinancialReportDispatcher.ExportAsync(_financialExports, request, ct);

        var exports = _exports ?? throw AccountingReportClientHandler.BooksDisabled();
        if (request.AfterLedgerSeq < 0)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"after must not be negative (was {request.AfterLedgerSeq}).");
        ClientRequestGuards.ThrowIfInvalidPage(0, request.Take);
        if (request.Since is { } since && request.Until is { } until && until <= since)
            throw new ClientException(ErrorCodes.InvalidOperation, "until must be after since.");

        try
        {
            var chunk = await exports.ExportAsync(new AccountingExportQuery(request.Format, request.AfterLedgerSeq,
                                                                            request.Take, request.Since,
                                                                            request.Until), ct);
            return new AccountingExportClientResponse(request.Format, chunk);
        }
        catch (AccountingBooksDisabledException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }
}

/// <summary>
/// The books' administration (ClientCommand 45, NL-602 A2): <c>accounting reconcile</c> and <c>rebuild</c> call the
/// books (refused when they are off); <c>accounting verify</c> walks the feed's hash chain in pages
/// (<see cref="AccountingChainVerifier"/>), books on or off, and (A3-T5) checks every period close.
/// </summary>
/// <remarks>
/// A3-T5: <c>close &lt;period&gt; [--force]</c>, <c>close list</c>, <c>close show &lt;period&gt;</c> and
/// <c>rebuild --book financial</c> go to <see cref="IAccountingPeriods"/>; a refused close, a bad period or the
/// financial book off is <c>invalid_operation</c> with the reason.
/// </remarks>
public sealed class AccountingAdminClientHandler
    : IClientCommandHandler<AccountingAdminClientRequest, AccountingAdminClientResponse>
{
    private readonly IAccountingBooks? _books;
    private readonly AccountNames _names;
    private readonly IAccountingPrices? _prices;
    private readonly IAccountingPeriods? _periods;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAccountingClassificationAdmin? _classification;
    private readonly IAccountingLots? _lots;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.AccountingAdmin;

    public AccountingAdminClientHandler(IUnitOfWork unitOfWork, IAccountingBooks? books,
                                        IOptions<AccountingOptions>? options = null,
                                        IAccountingClassificationAdmin? classification = null,
                                        IAccountingPrices? prices = null,
                                        IAccountingPeriods? periods = null,
                                        IAccountingLots? lots = null)
    {
        _lots = lots;
        _prices = prices;
        _unitOfWork = unitOfWork;
        _books = books;
        _names = (options?.Value ?? new AccountingOptions()).GetAccountNames();
        _classification = classification;
        _periods = periods;
    }

    /// <inheritdoc/>
    public async Task<AccountingAdminClientResponse> HandleAsync(AccountingAdminClientRequest request,
                                                                 CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = new AccountingAdminClientResponse(request.Action, _names);
        switch (request.Action)
        {
            case AccountingAdminAction.Verify:
                return response with
                {
                    Verification = await AccountingChainVerifier.VerifyAsync(_unitOfWork.AccountingEventDbRepository,
                                                                             cancellationToken: ct),
                    PeriodVerifications = _periods is null ? null : await _periods.VerifyClosesAsync(ct)
                };
            case AccountingAdminAction.Reconcile:
                return response with { Reconcile = await RequireBooks().ReconcileAsync(ct) };
            case AccountingAdminAction.Rebuild when request.Book is AccountingBook.Financial:
            case AccountingAdminAction.Close:
            case AccountingAdminAction.CloseList:
            case AccountingAdminAction.CloseShow:
                return await HandlePeriodsAsync(request, response, ct);
            case AccountingAdminAction.Rebuild:
                return response with { RebuiltEntries = await RequireBooks().RebuildAsync(ct) };
            case AccountingAdminAction.Classify:
                return response with
                {
                    Classify = await (_classification
                                   ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                                                "Accounting classification is not available."))
                                  .HandleAsync(request.Classify
                                            ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                                                         "A classify action is required."), ct)
                };
            case AccountingAdminAction.PricesImport:
            case AccountingAdminAction.PricesList:
            case AccountingAdminAction.PricesFetch:
            case AccountingAdminAction.PricesReplace:
                return response with { Prices = await AccountingPricesAdmin.HandleAsync(_prices, request, ct) };
            case AccountingAdminAction.LotsImport:
                return response with { LotImport = await ImportLotsAsync(request, ct) };
            default:
                throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting action {request.Action}.");
        }
    }

    /// <summary>The period close's actions (A3-T5); a refusal or a bad period is <c>invalid_operation</c>.</summary>
    private async Task<AccountingAdminClientResponse> HandlePeriodsAsync(AccountingAdminClientRequest request,
                                                                         AccountingAdminClientResponse response,
                                                                         CancellationToken ct)
    {
        try
        {
            switch (request.Action)
            {
                case AccountingAdminAction.Rebuild:
                    RequireBooks();
                    return response with { RebuiltEntries = await RequirePeriods().RebuildFinancialAsync(ct) };
                case AccountingAdminAction.Close:
                    return response with
                    {
                        Period = await RequirePeriods().CloseAsync(RequirePeriod(request), request.Force, ct)
                    };
                case AccountingAdminAction.CloseList:
                    return response with { Periods = await RequirePeriods().ListAsync(ct) };
                default:
                    var period = RequirePeriod(request);
                    return response with
                    {
                        Period = await RequirePeriods().GetAsync(period, ct)
                              ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                                           $"No accounting period {period}.")
                    };
            }
        }
        catch (AccountingCloseRefusedException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }

    /// <summary><c>lots import</c> (A3-T4): a refusal (the financial book off, a closed period, totals that differ) is
    /// <c>invalid_operation</c> with the reason.</summary>
    private async Task<AccountingLotImportResult> ImportLotsAsync(AccountingAdminClientRequest request,
                                                                  CancellationToken ct)
    {
        var lots = _lots ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                                      "The accounting lot import is not available on this node.");
        var arguments = request.Lots;
        if (arguments is null || arguments.Lots.Count == 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "No lots to import.");

        try
        {
            return await lots.ImportAsync(arguments.Currency, arguments.Lots, ct);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }

    private IAccountingBooks RequireBooks() =>
        _books is { IsEnabled: true } books ? books : throw AccountingReportClientHandler.BooksDisabled();

    private IAccountingPeriods RequirePeriods() =>
        _periods ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                              "This daemon does not serve accounting period closes.");

    private static string RequirePeriod(AccountingAdminClientRequest request) =>
        request.Period ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                                    "A period is required: YYYY-MM or YYYY-MM-DD..YYYY-MM-DD.");
}

/// <summary>The <c>short_channel_id</c> form of an accounting channel filter, resolved through the loaded channels.</summary>
internal static class AccountingChannelFilter
{
    /// <exception cref="ClientException">No loaded channel has that short channel id.</exception>
    public static ChannelId? ResolveScid(IChannelMemoryRepository? channels, ShortChannelId? scid)
    {
        if (scid is not { } value)
            return null;

        var channel = channels?.FindChannels(c => c.ShortChannelId != default && c.ShortChannelId == value)
                               .FirstOrDefault();
        return channel?.ChannelId
            ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                         $"No loaded channel has the short channel id {value}: name the channel by "
                                       + "its 64-hex channel id.");
    }
}