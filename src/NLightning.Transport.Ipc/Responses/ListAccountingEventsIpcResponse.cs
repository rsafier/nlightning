using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Models;
using Domain.Client.Responses;

/// <summary>
/// Response for ListAccountingEvents (ClientCommand 41, NL-602): a page of sealed accounting events in ledger order
/// and the cursor of the next page. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ListAccountingEventsIpcResponse
{
    /// <summary>The page, in ledger order.</summary>
    [Key(0)] public required List<AccountingEventIpcResponse> Events { get; init; }

    /// <summary>The cursor of the next page (pass it as <c>AfterLedgerSeq</c>): the last event's ledger sequence, or
    /// the request's cursor when the page is empty.</summary>
    [Key(1)] public required long NextAfter { get; init; }

    /// <summary>Whether the page is full: more events may follow <see cref="NextAfter"/>.</summary>
    [Key(2)] public required bool HasMore { get; init; }

    /// <summary>The last sealed ledger sequence of the whole feed.</summary>
    [Key(3)] public required long ChainTipLedgerSeq { get; init; }

    public static ListAccountingEventsIpcResponse FromClientResponse(ListAccountingEventsClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ListAccountingEventsIpcResponse
        {
            Events = response.Events.Select(AccountingEventIpcResponse.FromModel).ToList(),
            NextAfter = response.NextAfter,
            HasMore = response.HasMore,
            ChainTipLedgerSeq = response.ChainTipLedgerSeq
        };
    }
}

/// <summary>One sealed accounting event over the wire (NL-602). Amounts are signed msat from our side.</summary>
[MessagePackObject]
public sealed class AccountingEventIpcResponse
{
    /// <summary>The dense position in commit order.</summary>
    [Key(0)] public required long LedgerSeq { get; init; }

    /// <summary>The fact's key (<c>AccountingEventKeys</c>).</summary>
    [Key(1)] public required string EventKey { get; init; }

    /// <summary>The <c>AccountingEventKind</c> value.</summary>
    [Key(2)] public required int Kind { get; init; }

    /// <summary>The <c>AccountingEventKind</c> name (or the number when the daemon does not know it).</summary>
    [Key(3)] public required string KindName { get; init; }

    /// <summary>When the fact happened, Unix milliseconds.</summary>
    [Key(4)] public required long OccurredAtUnixMilliseconds { get; init; }

    /// <summary>The block of an on-chain fact.</summary>
    [Key(5)] public uint? BlockHeight { get; init; }

    /// <summary>Our balance change in msat (signed: more than 0 is more of our money).</summary>
    [Key(6)] public required long AmountMsat { get; init; }

    /// <summary>The fee we paid in msat.</summary>
    [Key(7)] public required long FeeMsat { get; init; }

    /// <summary>The channel, 64 hex characters.</summary>
    [Key(8)] public string? ChannelId { get; init; }

    /// <summary>The short channel id as <c>block x tx x output</c>.</summary>
    [Key(9)] public string? ShortChannelId { get; init; }

    /// <summary>The payment hash, 64 hex characters.</summary>
    [Key(10)] public string? PaymentHash { get; init; }

    /// <summary>The transaction, in display order (as bitcoind shows it).</summary>
    [Key(11)] public string? TxId { get; init; }

    /// <summary>The output of <see cref="TxId"/>.</summary>
    [Key(12)] public uint? OutputIndex { get; init; }

    /// <summary>The other node of the fact, 66 hex characters.</summary>
    [Key(13)] public string? Counterparty { get; init; }

    /// <summary>The <c>AccountingFinality</c> value (0 final, 1 confirmed, 2 irrevocable).</summary>
    [Key(14)] public required byte Finality { get; init; }

    /// <summary>The <c>AccountingFinality</c> name.</summary>
    [Key(15)] public required string FinalityName { get; init; }

    /// <summary>The <c>AccountingEventFlags</c> value.</summary>
    [Key(16)] public required int Flags { get; init; }

    /// <summary>Kind-specific details (string pairs).</summary>
    [Key(17)] public required Dictionary<string, string> Details { get; init; }

    /// <summary>The chain hash, 64 hex characters.</summary>
    [Key(18)] public string? Hash { get; init; }

    public static AccountingEventIpcResponse FromModel(AccountingEventModel accountingEvent)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);
        return new AccountingEventIpcResponse
        {
            LedgerSeq = accountingEvent.LedgerSeq ?? 0,
            EventKey = accountingEvent.EventKey,
            Kind = (int)accountingEvent.Kind,
            KindName = accountingEvent.Kind.ToString(),
            OccurredAtUnixMilliseconds = accountingEvent.OccurredAt.ToUnixTimeMilliseconds(),
            BlockHeight = accountingEvent.BlockHeight,
            AmountMsat = accountingEvent.AmountMsat,
            FeeMsat = accountingEvent.FeeMsat,
            ChannelId = accountingEvent.ChannelId?.ToString(),
            ShortChannelId = accountingEvent.ShortChannelId?.ToString(),
            PaymentHash = accountingEvent.PaymentHash?.ToString(),
            TxId = accountingEvent.TxId?.ToString(),
            OutputIndex = accountingEvent.OutputIndex,
            Counterparty = accountingEvent.Counterparty?.ToString(),
            Finality = (byte)accountingEvent.Finality,
            FinalityName = accountingEvent.Finality.ToString(),
            Flags = (int)accountingEvent.Flags,
            Details = new Dictionary<string, string>(accountingEvent.Details, StringComparer.Ordinal),
            Hash = accountingEvent.Hash is { } hash ? Convert.ToHexStringLower(hash) : null
        };
    }
}