namespace NLightning.Domain.Protocol.Payloads;

using Interfaces;
using ValueObjects;

/// <summary>
/// Represents the payload for the query_short_channel_ids message (BOLT 7, type 261).
/// </summary>
/// <param name="chainHash">The chain the short_channel_ids refer to.</param>
/// <param name="encodedShortIds">
/// The raw <c>encoded_short_ids</c>, including the leading encoding type byte (not validated here).
/// </param>
public class QueryShortChannelIdsPayload(ChainHash chainHash, ReadOnlyMemory<byte> encodedShortIds) : IMessagePayload
{
    /// <summary>
    /// The chain the short_channel_ids refer to.
    /// </summary>
    public ChainHash ChainHash { get; } = chainHash;

    /// <summary>
    /// The raw <c>encoded_short_ids</c>: the encoding type byte followed by the encoded array.
    /// </summary>
    public ReadOnlyMemory<byte> EncodedShortIds { get; } = encodedShortIds;
}