namespace NLightning.Domain.Protocol.Payloads;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Interfaces;

/// <summary>
/// The payload of <c>splice_ack</c> (BOLT 2 "Channel Splicing", type 81, SP-W-02):
/// <c>channel_id</c> ‖ <c>s64 funding_contribution_satoshis</c> ‖ <c>point funding_pubkey</c>.
/// </summary>
/// <param name="channelId">The channel being spliced.</param>
/// <param name="fundingContributionSatoshis">Signed contribution of the acceptor (0 when it does not contribute,
/// splicing plan D10).</param>
/// <param name="fundingPubKey">The acceptor's funding key for the new funding output.</param>
public sealed class SpliceAckPayload(ChannelId channelId, long fundingContributionSatoshis,
                                     CompactPubKey fundingPubKey) : IChannelMessagePayload
{
    /// <inheritdoc />
    public ChannelId ChannelId { get; } = channelId;

    /// <summary>The sender's signed contribution in satoshis (s64 on the wire).</summary>
    public long FundingContributionSatoshis { get; } = fundingContributionSatoshis;

    /// <summary>The sender's funding key for the new funding output.</summary>
    public CompactPubKey FundingPubKey { get; } = fundingPubKey;
}