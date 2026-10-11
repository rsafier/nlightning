namespace NLightning.Domain.Protocol.Payloads;

using Interfaces;
using ValueObjects;

/// <summary>
/// Represents the payload for the reply_short_channel_ids_end message (BOLT 7, type 262).
/// </summary>
/// <param name="chainHash">The chain of the query being answered.</param>
/// <param name="fullInformation">
/// <c>false</c> when the sender does not maintain up-to-date channel information for <paramref name="chainHash"/>.
/// </param>
public class ReplyShortChannelIdsEndPayload(ChainHash chainHash, bool fullInformation) : IMessagePayload
{
    /// <summary>
    /// The chain of the query being answered.
    /// </summary>
    public ChainHash ChainHash { get; } = chainHash;

    /// <summary>
    /// The <c>full_information</c> byte (1 = the sender maintains up-to-date channel information for the chain).
    /// </summary>
    public bool FullInformation { get; } = fullInformation;
}