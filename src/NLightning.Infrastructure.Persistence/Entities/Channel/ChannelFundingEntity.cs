// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// One funding output of a channel (splicing plan §3.3, §3.8, migration <c>AddSpliceFundings</c>): the initial funding,
/// a splice or a splice RBF attempt, in the <c>ChannelFunding</c> Domain record's fields. Keyed by (channel, funding
/// txid).
/// </summary>
/// <remarks>
/// The channel row's <c>FundingTxId</c>/<c>FundingOutputIndex</c>/<c>FundingAmountSatoshis</c> stay the current funding
/// (denormalized, written in the lock's save). The splice transaction itself lives in the interactive-tx session row.
/// </remarks>
public class ChannelFundingEntity
{
    public required ChannelId ChannelId { get; set; }

    public required TxId FundingTxId { get; set; }

    public required ushort OutputIndex { get; set; }

    public required long CapacitySatoshis { get; set; }

    public required CompactPubKey LocalFundingPubKey { get; set; }

    public required CompactPubKey RemoteFundingPubKey { get; set; }

    /// <summary>The derivation index of our funding key (splicing plan D5, 0 for the initial funding).</summary>
    public required uint LocalFundingKeyIndex { get; set; }

    public required long LocalBalanceDeltaMsat { get; set; }

    public required long RemoteBalanceDeltaMsat { get; set; }

    /// <summary><c>ChannelFundingKind</c> as a raw byte.</summary>
    public required byte Kind { get; set; }

    /// <summary><c>ChannelFundingStatus</c> as a raw byte.</summary>
    public required byte Status { get; set; }

    public uint? FeeratePerKw { get; set; }

    public uint? Locktime { get; set; }

    public TxId? RbfOf { get; set; }

    public uint? ConfirmedHeight { get; set; }

    public ShortChannelId? ShortChannelId { get; set; }

    public bool SpliceLockedSent { get; set; }

    public bool SpliceLockedReceived { get; set; }

    public bool AnnouncementSignaturesReceived { get; set; }

    /// <summary>
    /// The order the fundings of the channel were created in (0 for the initial funding): the order of
    /// <c>FundingSet.Pending</c> and of the batched <c>commitment_signed</c>s.
    /// </summary>
    public required int Sequence { get; set; }

    // Default constructor for EF Core
    internal ChannelFundingEntity() { }
}