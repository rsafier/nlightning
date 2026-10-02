using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Accounting;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
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
    private readonly AccountNames _names;
    private readonly IAccountingReports? _reports;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.AccountingReport;

    public AccountingReportClientHandler(IAccountingReports? reports, IOptions<AccountingOptions>? options = null,
                                         IChannelMemoryRepository? channelMemoryRepository = null)
    {
        _reports = reports;
        _names = (options?.Value ?? new AccountingOptions()).GetAccountNames();
        _channelMemoryRepository = channelMemoryRepository;
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

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.AccountingExport;

    public AccountingExportClientHandler(IAccountingExports? exports)
    {
        _exports = exports;
    }

    /// <inheritdoc/>
    public async Task<AccountingExportClientResponse> HandleAsync(AccountingExportClientRequest request,
                                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
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
/// (<see cref="AccountingChainVerifier"/>), books on or off.
/// </summary>
public sealed class AccountingAdminClientHandler
    : IClientCommandHandler<AccountingAdminClientRequest, AccountingAdminClientResponse>
{
    private readonly IAccountingBooks? _books;
    private readonly AccountNames _names;
    private readonly IAccountingPrices? _prices;
    private readonly IUnitOfWork _unitOfWork;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.AccountingAdmin;

    public AccountingAdminClientHandler(IUnitOfWork unitOfWork, IAccountingBooks? books,
                                        IOptions<AccountingOptions>? options = null,
                                        IAccountingPrices? prices = null)
    {
        _prices = prices;
        _unitOfWork = unitOfWork;
        _books = books;
        _names = (options?.Value ?? new AccountingOptions()).GetAccountNames();
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
                                                                             cancellationToken: ct)
                };
            case AccountingAdminAction.Reconcile:
                return response with { Reconcile = await RequireBooks().ReconcileAsync(ct) };
            case AccountingAdminAction.Rebuild:
                return response with { RebuiltEntries = await RequireBooks().RebuildAsync(ct) };
            case AccountingAdminAction.PricesImport:
            case AccountingAdminAction.PricesList:
            case AccountingAdminAction.PricesFetch:
                return response with { Prices = await AccountingPricesAdmin.HandleAsync(_prices, request, ct) };
            default:
                throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting action {request.Action}.");
        }
    }

    private IAccountingBooks RequireBooks() =>
        _books is { IsEnabled: true } books ? books : throw AccountingReportClientHandler.BooksDisabled();
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