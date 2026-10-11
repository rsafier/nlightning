namespace NLightning.Domain.Protocol.Payloads;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Interfaces;

/// <summary>
/// The payload of <c>splice_init</c> (BOLT 2 "Channel Splicing", type 80, SP-W-01):
/// <c>channel_id</c> ‖ <c>s64 funding_contribution_satoshis</c> ‖ <c>u32 funding_feerate_perkw</c> ‖
/// <c>u32 locktime</c> ‖ <c>point funding_pubkey</c>.
/// </summary>
/// <param name="channelId">The channel being spliced.</param>
/// <param name="fundingContributionSatoshis">Signed: positive adds to the sender's channel balance (splice-in),
/// negative subtracts from it (splice-out), SP-S-02.</param>
/// <param name="fundingFeeratePerKw">The splice transaction's feerate.</param>
/// <param name="locktime">The splice transaction's <c>nLockTime</c>.</param>
/// <param name="fundingPubKey">The sender's funding key for the new funding output (SHOULD differ from the previous
/// one, splicing plan D5).</param>
public sealed class SpliceInitPayload(ChannelId channelId, long fundingContributionSatoshis, uint fundingFeeratePerKw,
                                      uint locktime, CompactPubKey fundingPubKey) : IChannelMessagePayload
{
    /// <inheritdoc />
    public ChannelId ChannelId { get; } = channelId;

    /// <summary>The sender's signed contribution in satoshis (s64 on the wire).</summary>
    public long FundingContributionSatoshis { get; } = fundingContributionSatoshis;

    /// <summary>The feerate of the splice transaction, in sat/kw.</summary>
    public uint FundingFeeratePerKw { get; } = fundingFeeratePerKw;

    /// <summary>The <c>nLockTime</c> of the splice transaction.</summary>
    public uint Locktime { get; } = locktime;

    /// <summary>The sender's funding key for the new funding output.</summary>
    public CompactPubKey FundingPubKey { get; } = fundingPubKey;
}