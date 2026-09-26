namespace NLightning.Application.Gossip.Sync;

using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Queries;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Collects the <c>reply_channel_range</c> messages answering one of our <c>query_channel_range</c>s and checks them
/// against BOLT 7 (B7-Q-04, plan G3-T2): the chain is ours; the first reply has <c>first_blocknum</c> &lt;= the
/// query's and ends after it; each later reply has a <c>first_blocknum</c> no lower than the previous one, or starts
/// where the previous one ended (see below); the reply with <c>sync_complete</c> = 1 reaches the query's end;
/// <c>encoded_short_ids</c> and the TLVs decode (encoding 0). A violation is a <see cref="WarningException"/>: the
/// sync with that peer ends with a <c>warning</c>.
/// </summary>
/// <remarks>
/// <para>
/// Core Lightning (seen on mainnet, 2026-09) answers with replies whose short channel ids are not in ascending order
/// and whose <c>number_of_blocks</c> is the 32-bit difference between the last and the first one's block, so it wraps
/// (e.g. <c>918664 + 4294956303</c>): the next reply then starts at the wrapped end (907671), below the previous
/// <c>first_blocknum</c>. LND accepts that because it checks continuity in 32-bit arithmetic (a reply must start at
/// the previous reply's last block or the one after); we accept the same continuation (a later reply whose
/// <c>first_blocknum</c> is the previous reply's wrapped end or the block before it), so a CLN peer can be synced
/// from (NL-379). Only a reply with <c>sync_complete</c> = 1 can end the sync, and it must reach the query's end.
/// </para>
/// <para>
/// Short channel ids outside the query are dropped; ids outside the blocks their own reply claims to cover are kept
/// (not a violation: BOLT 7 has no rule for them, the CLN replies above carry them, and every id we then ask for is
/// validated like any other gossip).
/// </para>
/// </remarks>
internal sealed class RangeReplyCollector
{
    private readonly ChainHash _chainHash;
    private readonly uint _queryFirst;
    private readonly ulong _queryEnd;
    private readonly Dictionary<ShortChannelId, ChannelUpdatePair?> _entries = [];
    private uint? _previousFirst;
    private uint _previousWrappedEnd;

    /// <param name="chainHash">Our chain (the query's).</param>
    /// <param name="queryFirst">The query's <c>first_blocknum</c>.</param>
    /// <param name="queryNumberOfBlocks">The query's <c>number_of_blocks</c>.</param>
    public RangeReplyCollector(ChainHash chainHash, uint queryFirst, uint queryNumberOfBlocks)
    {
        _chainHash = chainHash;
        _queryFirst = queryFirst;
        // A reply cannot reach past 0xFFFFFFFF (first + number is a u32 sum); LND caps the same way
        _queryEnd = Math.Min((ulong)queryFirst + queryNumberOfBlocks, uint.MaxValue);
    }

    /// <summary>True once the final reply (covering the query, <c>sync_complete</c> = 1) arrived.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>How many replies were accepted.</summary>
    public int ReplyCount { get; private set; }

    /// <summary>
    /// The channels collected so far, ascending, each with its update timestamps when the peer sent them.
    /// </summary>
    public IReadOnlyList<KeyValuePair<ShortChannelId, ChannelUpdatePair?>> Entries =>
        _entries.OrderBy(e => QueryResponder.ToUInt64(e.Key)).ToList();

    /// <summary>
    /// Checks and adds one reply.
    /// </summary>
    /// <exception cref="WarningException">The reply breaks a BOLT 7 rule, or a reply arrived after the final one.
    /// </exception>
    public void Add(ReplyChannelRangeMessage reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var payload = reply.Payload;
        if (IsComplete)
            throw new WarningException("reply_channel_range: received after the final reply");
        if (payload.ChainHash != _chainHash)
            throw new WarningException("reply_channel_range: chain_hash is not the one we queried");

        var first = payload.FirstBlocknum;
        var end = (ulong)first + payload.NumberOfBlocks;
        if (_previousFirst is null)
        {
            if (first > _queryFirst || end <= _queryFirst)
                throw new WarningException(
                    $"reply_channel_range: the first reply ({first}+{payload.NumberOfBlocks}) does not start the "
                  + $"queried range at {_queryFirst}");
        }
        else if (first < _previousFirst.Value && first != _previousWrappedEnd
                                                 && first != unchecked(_previousWrappedEnd - 1))
        {
            throw new WarningException(
                $"reply_channel_range: first_blocknum {first} is lower than the previous reply's {_previousFirst} "
              + $"and does not continue it (previous end {_previousWrappedEnd})");
        }

        var shortChannelIds = GossipQueryCodec.DecodeShortChannelIds(payload.EncodedShortIds.Span,
                                                                     "reply_channel_range");
        var timestamps = reply.TimestampsTlv is null
                             ? null
                             : GossipQueryCodec.DecodeTimestamps(reply.TimestampsTlv.Value, shortChannelIds.Length);
        if (reply.ChecksumsTlv is not null)
            _ = GossipQueryCodec.DecodeChecksums(reply.ChecksumsTlv.Value, shortChannelIds.Length);

        if (payload.SyncComplete && end < _queryEnd)
            throw new WarningException(
                $"reply_channel_range: the final reply ends at block {end}, before the queried end {_queryEnd}");

        for (var i = 0; i < shortChannelIds.Length; i++)
        {
            var height = shortChannelIds[i].BlockHeight;
            if (height < _queryFirst || height >= _queryEnd)
                continue;

            _entries[shortChannelIds[i]] = timestamps?[i];
        }

        _previousFirst = first;
        _previousWrappedEnd = unchecked(first + payload.NumberOfBlocks);
        ReplyCount++;
        IsComplete = payload.SyncComplete;
    }
}