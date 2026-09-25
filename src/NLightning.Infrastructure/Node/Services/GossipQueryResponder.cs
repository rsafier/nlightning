namespace NLightning.Infrastructure.Node.Services;

using Domain.Exceptions;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// Builds the BOLT 7 answers to gossip queries for a node that keeps no gossip map.
/// </summary>
/// <remarks>
/// NLightning does not store or announce public channels yet, so every query is answered with "nothing known":
/// one <c>reply_channel_range</c> covering the whole requested range with an empty <c>encoded_short_ids</c>, and a
/// bare <c>reply_short_channel_ids_end</c> with <c>full_information</c> = 0. When gossip lands, replace these with
/// answers built from the graph.
/// </remarks>
internal static class GossipQueryResponder
{
    /// <summary>
    /// BOLT 7 encoding type 0: an uncompressed array of <c>short_channel_id</c>s in ascending order.
    /// </summary>
    internal const byte EncodingUncompressed = 0;

    private const int ShortChannelIdLength = 8;

    /// <summary>
    /// An empty <c>encoded_short_ids</c>: just the encoding type byte.
    /// </summary>
    private static readonly byte[] s_emptyEncodedShortIds = [EncodingUncompressed];

    /// <summary>
    /// Answers a <c>query_channel_range</c> with a single, final <c>reply_channel_range</c>.
    /// </summary>
    /// <remarks>
    /// BOLT 7: the first reply MUST have <c>first_blocknum</c> &lt;= the query's and <c>first_blocknum</c> +
    /// <c>number_of_blocks</c> &gt; the query's <c>first_blocknum</c>; the final one MUST cover the query's end and
    /// set <c>sync_complete</c>. Echoing the requested range (at least one block) satisfies both in one message.
    /// </remarks>
    public static ReplyChannelRangeMessage CreateReply(QueryChannelRangeMessage query)
    {
        var payload = query.Payload;
        var numberOfBlocks = Math.Max(payload.NumberOfBlocks, 1u);

        return new ReplyChannelRangeMessage(new ReplyChannelRangePayload(payload.ChainHash, payload.FirstBlocknum,
                                                                         numberOfBlocks, true,
                                                                         s_emptyEncodedShortIds));
    }

    /// <summary>
    /// Answers a <c>query_short_channel_ids</c> with <c>reply_short_channel_ids_end</c>.
    /// </summary>
    /// <remarks>
    /// We know none of the queried channels, so there is nothing to send before the end marker. BOLT 7: a node that
    /// "does not maintain up-to-date channel information for <c>chain_hash</c>" MUST set <c>full_information</c> to 0.
    /// </remarks>
    /// <exception cref="WarningException">
    /// The query is malformed (unknown encoding, partial <c>short_channel_id</c>, bad <c>query_flags</c>); BOLT 7
    /// lets the receiver send a warning.
    /// </exception>
    public static ReplyShortChannelIdsEndMessage CreateReply(QueryShortChannelIdsMessage query)
    {
        var shortChannelIdCount = ValidateEncodedShortIds(query.Payload.EncodedShortIds.Span);

        if (query.QueryFlagsTlv is not null)
            ValidateQueryFlags(query.QueryFlagsTlv.Value, shortChannelIdCount);

        return new ReplyShortChannelIdsEndMessage(
            new ReplyShortChannelIdsEndPayload(query.Payload.ChainHash, false));
    }

    private static int ValidateEncodedShortIds(ReadOnlySpan<byte> encodedShortIds)
    {
        if (encodedShortIds.IsEmpty)
            throw new WarningException("query_short_channel_ids: encoded_short_ids has no encoding type");

        if (encodedShortIds[0] != EncodingUncompressed)
            throw new WarningException(
                $"query_short_channel_ids: unknown encoded_short_ids encoding type {encodedShortIds[0]}");

        var dataLength = encodedShortIds.Length - 1;
        if (dataLength % ShortChannelIdLength != 0)
            throw new WarningException(
                "query_short_channel_ids: encoded_short_ids is not a whole number of short_channel_ids");

        return dataLength / ShortChannelIdLength;
    }

    private static void ValidateQueryFlags(ReadOnlySpan<byte> queryFlags, int shortChannelIdCount)
    {
        if (queryFlags.IsEmpty)
            throw new WarningException("query_short_channel_ids: query_flags has no encoding type");

        if (queryFlags[0] != EncodingUncompressed)
            throw new WarningException(
                $"query_short_channel_ids: unknown query_flags encoding type {queryFlags[0]}");

        // Count the bigsize flags (one per short_channel_id)
        var flags = 0;
        var offset = 1;
        while (offset < queryFlags.Length)
        {
            offset += queryFlags[offset] switch
            {
                0xFF => 9,
                0xFE => 5,
                0xFD => 3,
                _ => 1
            };
            flags++;
        }

        if (offset != queryFlags.Length || flags != shortChannelIdCount)
            throw new WarningException(
                "query_short_channel_ids: query_flags does not decode to one flag per short_channel_id");
    }
}