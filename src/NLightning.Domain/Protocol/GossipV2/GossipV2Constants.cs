using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.GossipV2;

/// <summary>
/// The TLV types and constants of taproot gossip (BOLTs PR #1059, draft head <c>4eef3dfa</c> of 2026-05-22). Every
/// v2 message is a pure TLV stream; the numbers are per message and overlap.
/// </summary>
[ExcludeFromCodeCoverage]
public static class GossipV2Constants
{
    /// <summary>The <c>signature</c> record of every v2 gossip message (the first unsigned type).</summary>
    public const ulong SignatureType = 240;

    /// <summary>
    /// <c>max_backdate_blocks</c>: how far below the current block height a sender may date a
    /// <c>channel_update_2</c>/<c>node_announcement_2</c> (and receivers ignore older ones).
    /// </summary>
    public const uint MaxBackdateBlocks = 2016;

    /// <summary>The length of a BIP 340 signature.</summary>
    public const int SignatureLength = 64;

    /// <summary>The <c>channel_announcement_2</c> message name of its MsgHash tag.</summary>
    public const string ChannelAnnouncement2Name = "channel_announcement_2";

    /// <summary>The <c>channel_update_2</c> message name of its MsgHash tag.</summary>
    public const string ChannelUpdate2Name = "channel_update_2";

    /// <summary>The <c>node_announcement_2</c> message name of its MsgHash tag.</summary>
    public const string NodeAnnouncement2Name = "node_announcement_2";

    /// <summary><c>channel_announcement_2</c> (267) TLV types.</summary>
    public static class ChannelAnnouncement2
    {
        public const ulong ChainHash = 0;
        public const ulong Features = 2;
        public const ulong ShortChannelId = 4;
        public const ulong Capacity = 6;
        public const ulong NodeId1 = 8;
        public const ulong NodeId2 = 10;
        public const ulong BitcoinKey1 = 12;
        public const ulong BitcoinKey2 = 14;
        public const ulong MerkleRootHash = 16;
        public const ulong Outpoint = 18;
        public const ulong Signature = SignatureType;
    }

    /// <summary><c>channel_update_2</c> (271) TLV types.</summary>
    public static class ChannelUpdate2
    {
        public const ulong ChainHash = 0;
        public const ulong ShortChannelId = 2;
        public const ulong BlockHeight = 4;
        public const ulong DisableFlags = 6;
        public const ulong CltvExpiryDelta = 10;
        public const ulong HtlcMinimumMsat = 12;
        public const ulong HtlcMaximumMsat = 14;
        public const ulong FeeBaseMsat = 16;
        public const ulong FeeProportionalMillionths = 18;
        public const ulong InboundFeeBaseMsat = 20;
        public const ulong InboundFeeProportionalMillionths = 22;
        public const ulong Signature = SignatureType;

        /// <summary>Default <c>cltv_expiry_delta</c> when the record is absent.</summary>
        public const ushort DefaultCltvExpiryDelta = 80;

        /// <summary>Default <c>htlc_minimum_msat</c> when the record is absent.</summary>
        public const ulong DefaultHtlcMinimumMsat = 1;

        /// <summary>Default <c>fee_base_msat</c> when the record is absent.</summary>
        public const uint DefaultFeeBaseMsat = 1000;

        /// <summary>Default <c>fee_proportional_millionths</c> when the record is absent.</summary>
        public const uint DefaultFeeProportionalMillionths = 1;
    }

    /// <summary><c>node_announcement_2</c> (269) TLV types.</summary>
    public static class NodeAnnouncement2
    {
        public const ulong Features = 0;
        public const ulong Color = 1;
        public const ulong BlockHeight = 2;
        public const ulong Alias = 3;
        public const ulong NodeId = 4;
        public const ulong Ipv4Addresses = 5;
        public const ulong Ipv6Addresses = 7;
        public const ulong TorV3Addresses = 9;
        public const ulong DnsHostnames = 11;
        public const ulong Signature = SignatureType;

        /// <summary>The longest alias, in bytes of UTF-8.</summary>
        public const int MaxAliasLength = 32;
    }

    /// <summary><c>announcement_signatures_2</c> (260) TLV types.</summary>
    public static class AnnouncementSignatures2
    {
        public const ulong ChannelId = 0;
        public const ulong ShortChannelId = 2;
        public const ulong PartialSignatures = 4;
        public const ulong FundingTxId = 6;
    }
}