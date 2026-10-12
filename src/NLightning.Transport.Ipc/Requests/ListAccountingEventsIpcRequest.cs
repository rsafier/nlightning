using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Accounting.Enums;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;

/// <summary>
/// Request for ListAccountingEvents (ClientCommand 41, NL-602): a page of sealed accounting events in ledger order
/// after a cursor, with optional filters. Every filter key is nullable (null = off); keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ListAccountingEventsIpcRequest
{
    /// <summary>Only events after this ledger sequence (0 = from the start): the previous page's
    /// <c>NextAfter</c>.</summary>
    [Key(0)] public long AfterLedgerSeq { get; set; }

    /// <summary>The most events to return.</summary>
    [Key(1)] public int Limit { get; set; } = 100;

    /// <summary>Only these <c>AccountingEventKind</c> values, or null for every kind.</summary>
    [Key(2)] public List<int>? Kinds { get; set; }

    /// <summary>Only events of this channel: a 64-hex-character channel id, or a <c>short_channel_id</c> as
    /// <c>block x tx x output</c> (or its decimal form) of a loaded channel.</summary>
    [Key(3)] public string? Channel { get; set; }

    /// <summary>Only events that happened at or after this Unix time (seconds), or null for no lower bound.</summary>
    [Key(4)] public long? SinceUnixSeconds { get; set; }

    /// <summary>Only events that happened before this Unix time (seconds), or null for no upper bound.</summary>
    [Key(5)] public long? UntilUnixSeconds { get; set; }

    /// <exception cref="ClientException">An unknown kind or a channel that is neither form.</exception>
    public ListAccountingEventsClientRequest ToClientRequest()
    {
        var (channelId, channelScid) = ChannelFilterText.Parse(Channel);
        List<AccountingEventKind>? kinds = null;
        if (Kinds is { Count: > 0 })
        {
            kinds = [];
            foreach (var kind in Kinds)
            {
                if (!Enum.IsDefined(typeof(AccountingEventKind), kind))
                    throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting event kind {kind}.");

                kinds.Add((AccountingEventKind)kind);
            }
        }

        return new ListAccountingEventsClientRequest
        {
            AfterLedgerSeq = AfterLedgerSeq,
            Take = Limit,
            Kinds = kinds,
            ChannelId = channelId,
            ChannelScid = channelScid,
            Since = SinceUnixSeconds is { } since ? DateTimeOffset.FromUnixTimeSeconds(since) : null,
            Until = UntilUnixSeconds is { } until ? DateTimeOffset.FromUnixTimeSeconds(until) : null
        };
    }
}