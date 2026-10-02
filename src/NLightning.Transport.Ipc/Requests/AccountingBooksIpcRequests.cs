using System.Globalization;
using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial.Reports;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;

/// <summary>
/// Request for AccountingReport (ClientCommand 43, NL-602 A2): one report of the operational books. Every filter key is
/// nullable (null = off); keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingReportIpcRequest
{
    /// <summary>The <c>AccountingReportKind</c> value (1 balance sheet, 2 income statement, 3 channels, 4 peers, 5 fees,
    /// 6 register).</summary>
    [Key(0)] public int Kind { get; set; } = (int)AccountingReportKind.BalanceSheet;

    /// <summary>The start of the period, Unix seconds (inclusive).</summary>
    [Key(1)] public long? SinceUnixSeconds { get; set; }

    /// <summary>The end of the period, Unix seconds (exclusive); the time of a balance sheet.</summary>
    [Key(2)] public long? UntilUnixSeconds { get; set; }

    /// <summary>Only this channel: a 64-hex channel id or a <c>short_channel_id</c> of a loaded channel.</summary>
    [Key(3)] public string? Channel { get; set; }

    /// <summary>Only entries posting to this account (register): a role name or an account name.</summary>
    [Key(4)] public string? Account { get; set; }

    /// <summary>Only these <c>AccountingEventKind</c> values (register).</summary>
    [Key(5)] public List<int>? EventKinds { get; set; }

    /// <summary>The register's cursor (entries after this ledger sequence).</summary>
    [Key(6)] public long AfterLedgerSeq { get; set; }

    /// <summary>The register's page size.</summary>
    [Key(7)] public int Limit { get; set; } = 100;

    /// <summary>The <c>AccountingBook</c> value (0 operational, 1 financial; NL-602 A3-T6), null = operational.</summary>
    [Key(8)] public int? Book { get; set; }

    /// <summary>The fiat currency of a financial report (an ISO 4217 code), null = the default (USD).</summary>
    [Key(9)] public string? Currency { get; set; }

    /// <summary>A BTC price for the market values, as an invariant decimal string (<c>86048.5</c>).</summary>
    [Key(10)] public string? Price { get; set; }

    /// <summary>The <c>AccountingGainsGrouping</c> value of the realized gains (1 month, 2 quarter, 3 year, 4 total),
    /// null = month.</summary>
    [Key(11)] public int? Grouping { get; set; }

    /// <summary>The financial register's cursor adjustment (with <see cref="AfterLedgerSeq"/>), null = none.</summary>
    [Key(12)] public int? AfterAdjustment { get; set; }

    /// <exception cref="ClientException">An unknown report, event kind, book or grouping, a bad price, or a channel
    /// that is neither form.</exception>
    public AccountingReportClientRequest ToClientRequest()
    {
        if (!Enum.IsDefined(typeof(AccountingReportKind), Kind))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting report {Kind}.");
        if (Book is { } book && (book is < 0 or > byte.MaxValue || !Enum.IsDefined((AccountingBook)(byte)book)))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting book {book}.");
        if (Grouping is { } grouping && !Enum.IsDefined(typeof(AccountingGainsGrouping), grouping))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown gains grouping {grouping}.");

        decimal? price = null;
        if (!string.IsNullOrWhiteSpace(Price))
        {
            if (!decimal.TryParse(Price.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                                  out var parsed) || parsed <= 0m)
                throw new ClientException(ErrorCodes.InvalidOperation,
                                          $"Invalid price '{Price}': expected a number above zero such as 86048.5.");
            price = parsed;
        }

        var (channelId, channelScid) = ChannelFilterText.Parse(Channel);
        List<AccountingEventKind>? kinds = null;
        if (EventKinds is { Count: > 0 })
        {
            kinds = [];
            foreach (var kind in EventKinds)
            {
                if (!Enum.IsDefined(typeof(AccountingEventKind), kind))
                    throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting event kind {kind}.");

                kinds.Add((AccountingEventKind)kind);
            }
        }

        return new AccountingReportClientRequest
        {
            Kind = (AccountingReportKind)Kind,
            Since = SinceUnixSeconds is { } since ? DateTimeOffset.FromUnixTimeSeconds(since) : null,
            Until = UntilUnixSeconds is { } until ? DateTimeOffset.FromUnixTimeSeconds(until) : null,
            ChannelId = channelId,
            ChannelScid = channelScid,
            Account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim(),
            EventKinds = kinds,
            AfterLedgerSeq = AfterLedgerSeq,
            Take = Limit,
            Book = Book is { } value ? (AccountingBook)value : AccountingBook.Operational,
            Currency = string.IsNullOrWhiteSpace(Currency) ? null : Currency.Trim(),
            Price = price,
            Grouping = Grouping is { } periods ? (AccountingGainsGrouping)periods : AccountingGainsGrouping.Month,
            AfterAdjustment = AfterAdjustment
        };
    }
}

/// <summary>
/// Request for AccountingExport (ClientCommand 44, NL-602 A2): one page of an export of the books, streamed back in
/// the response (the daemon never writes a file). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingExportIpcRequest
{
    /// <summary>The <c>AccountingExportFormat</c> value (1 hledger, 2 beancount, 3 CSV).</summary>
    [Key(0)] public int Format { get; set; } = (int)AccountingExportFormat.Hledger;

    /// <summary>The start of the period, Unix seconds (inclusive).</summary>
    [Key(1)] public long? SinceUnixSeconds { get; set; }

    /// <summary>The end of the period, Unix seconds (exclusive).</summary>
    [Key(2)] public long? UntilUnixSeconds { get; set; }

    /// <summary>Entries after this ledger sequence: 0 for the first page (with the header), then the previous page's
    /// <c>NextAfter</c>.</summary>
    [Key(3)] public long AfterLedgerSeq { get; set; }

    /// <summary>The page size in entries.</summary>
    [Key(4)] public int Limit { get; set; } = 1_000;

    /// <summary>The <c>AccountingBook</c> value (0 operational, 1 financial; NL-602 A3-T6), null = operational.</summary>
    [Key(5)] public int? Book { get; set; }

    /// <summary>The fiat currency of a financial export's costs and prices, null = the default (USD).</summary>
    [Key(6)] public string? Currency { get; set; }

    /// <summary>A financial export's cursor adjustment: the previous page's <c>NextAfterAdjustment</c>, null on the
    /// first page.</summary>
    [Key(7)] public int? AfterAdjustment { get; set; }

    /// <exception cref="ClientException">An unknown format or book.</exception>
    public AccountingExportClientRequest ToClientRequest()
    {
        if (!Enum.IsDefined(typeof(AccountingExportFormat), Format))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown export format {Format}.");
        if (Book is { } book && (book is < 0 or > byte.MaxValue || !Enum.IsDefined((AccountingBook)(byte)book)))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting book {book}.");

        return new AccountingExportClientRequest
        {
            Format = (AccountingExportFormat)Format,
            Since = SinceUnixSeconds is { } since ? DateTimeOffset.FromUnixTimeSeconds(since) : null,
            Until = UntilUnixSeconds is { } until ? DateTimeOffset.FromUnixTimeSeconds(until) : null,
            AfterLedgerSeq = AfterLedgerSeq,
            Take = Limit,
            Book = Book is { } value ? (AccountingBook)value : AccountingBook.Operational,
            Currency = string.IsNullOrWhiteSpace(Currency) ? null : Currency.Trim(),
            AfterAdjustment = AfterAdjustment
        };
    }
}

/// <summary>
/// Request for AccountingAdmin (ClientCommand 45, NL-602 A2): reconcile, rebuild or verify. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingAdminIpcRequest
{
    /// <summary>The <c>AccountingAdminAction</c> value (1 reconcile, 2 rebuild, 3 verify, 20 close, 21 close list,
    /// 22 close show).</summary>
    [Key(0)] public int Action { get; set; } = (int)AccountingAdminAction.Verify;

    /// <summary>The classify action (action 5, NL-602 A3-T3).</summary>
    [Key(2)] public AccountingClassifyIpcRequest? Classify { get; set; }

    /// <summary>The arguments of the <c>prices</c> actions (10 import, 11 list, 12 fetch; NL-602 A3-T2).</summary>
    [Key(10)] public AccountingPricesIpcRequest? Prices { get; set; }

    // Keys 20-22 are A3-T5's (period close), apart from the other A3 lanes' keys

    /// <summary>The period of <c>close</c> and <c>close show</c>: <c>YYYY-MM</c> or <c>YYYY-MM-DD..YYYY-MM-DD</c>.</summary>
    [Key(20)] public string? Period { get; set; }

    /// <summary><c>close --force</c>.</summary>
    [Key(21)] public bool Force { get; set; }

    /// <summary>The <c>AccountingBook</c> of <c>rebuild --book</c> (0 operational, 1 financial; null = operational).</summary>
    [Key(22)] public int? Book { get; set; }

    /// <exception cref="ClientException">An unknown action or book, or bad <c>prices</c> arguments.</exception>
    public AccountingAdminClientRequest ToClientRequest()
    {
        if (!Enum.IsDefined(typeof(AccountingAdminAction), Action))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting action {Action}.");
        if (Book is { } book && (book is < 0 or > byte.MaxValue || !Enum.IsDefined((AccountingBook)(byte)book)))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting book {Book}.");

        return new AccountingAdminClientRequest
        {
            Action = (AccountingAdminAction)Action,
            Classify = Classify?.ToClientRequest(),
            Prices = Prices?.ToClientRequest(),
            Period = string.IsNullOrWhiteSpace(Period) ? null : Period.Trim(),
            Force = Force,
            Book = Book is { } value ? (AccountingBook)value : null
        };
    }
}