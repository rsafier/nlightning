namespace NLightning.Infrastructure.Persistence.Entities.Gossip;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A BOLT 7 <c>channel_announcement</c> we accepted (migration <c>AddGossipGraph</c>, BOLT 7 plan G2-T3). Keyed by the
/// short channel id (its 8 wire bytes, so byte order is numeric order); the raw payload is kept for relay.
/// </summary>
public class GraphChannelEntity
{
    public required ShortChannelId ShortChannelId { get; set; }
    public required CompactPubKey NodeId1 { get; set; }
    public required CompactPubKey NodeId2 { get; set; }
    public required CompactPubKey BitcoinKey1 { get; set; }
    public required CompactPubKey BitcoinKey2 { get; set; }

    /// <summary>The funding output's amount.</summary>
    public required long CapacitySat { get; set; }

    public required byte[] Features { get; set; }

    /// <summary>The whole payload (signatures included, message type excluded).</summary>
    public required byte[] RawAnnouncement { get; set; }

    /// <summary><c>Domain.Gossip.Persistence.GraphChannelVerification</c>.</summary>
    public required byte Verification { get; set; }

    /// <summary>The height of the block that spent the funding output, or null while unspent.</summary>
    public uint? SpentAtHeight { get; set; }

    /// <summary>
    /// The funding transaction id, when known (migration <c>AddGraphFundingTxId</c>, NL-352): the pruner's spent
    /// check needs it, and rows stored without one are looked up again at startup.
    /// </summary>
    public TxId? FundingTxId { get; set; }

    /// <summary>When it was received; UTC ticks (<c>UtcTicksConverter</c>).</summary>
    public required DateTimeOffset ReceivedAt { get; set; }

    internal GraphChannelEntity()
    {
    }
}