namespace NLightning.Domain.Tests.Gossip;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Validation;
using Domain.Protocol.GossipV2;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Tlvs = Domain.Protocol.GossipV2.GossipV2Constants.ChannelAnnouncement2;

/// <summary>
/// The pure receiver rules of taproot gossip (BOLTs PR #1059 draft, NL-878): <see cref="GossipV2Validator"/>.
/// </summary>
public class GossipV2ValidatorTests
{
    private const uint Tip = 3_000;
    private static readonly ChainHash s_chain = BitcoinNetwork.Regtest.ChainHash;
    private static readonly ShortChannelId s_scid = new(900, 2, 1);
    private static readonly CompactPubKey s_node1 = Key(0x02, 1);
    private static readonly CompactPubKey s_node2 = Key(0x03, 2);
    private static readonly GossipValidationContext s_context = new(s_chain, 1_700_000_000, Tip);

    private static CompactPubKey Key(byte prefix, byte last)
    {
        var bytes = new byte[33];
        bytes[0] = prefix;
        bytes[32] = last;
        return new CompactPubKey(bytes);
    }

    private static ChannelAnnouncement2Payload Announcement(ChainHash? chain = null, ShortChannelId? scid = null,
                                                            ReadOnlySpan<byte> features = default) =>
        ChannelAnnouncement2Payload.Create(chain ?? s_chain, features, scid ?? s_scid, 1_000_000, s_node1, s_node2,
                                           Key(0x02, 3), Key(0x02, 4), ReadOnlySpan<byte>.Empty,
                                           new TxId(new byte[32]), 1);

    private static ChannelUpdate2Payload Update(uint blockHeight, byte direction = 0, ChainHash? chain = null,
                                                byte disableFlags = 0, ulong htlcMaximumMsat = 500_000_000,
                                                uint feeBase = 1_000) =>
        ChannelUpdate2Payload.Create(chain ?? s_chain, s_scid, direction, blockHeight, disableFlags, 40, 1_000,
                                     htlcMaximumMsat, feeBase, 100);

    private static GraphChannel V2Channel(GraphPolicy? policy = null, uint? spentAt = null)
    {
        var channel = new GraphChannel(s_scid, s_node1, s_node2, null, null, 1_000_000)
        {
            Versions = GraphGossipVersions.V2,
            RawAnnouncement2 = Announcement().GetBytes(),
            SpentAtHeight = spentAt
        };
        return policy is null ? channel : channel.WithPolicy(policy);
    }

    [Fact]
    public void Given_AValidAnnouncement2_When_Validating_Then_Accepted()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelAnnouncement2(Announcement(), s_context);

        // Assert
        Assert.Equal(GossipValidationOutcome.Accept, result.Outcome);
        Assert.True(result.Routable);
    }

    [Fact]
    public void Given_UnorderedNodeIds_When_Validating_Then_Warned()
    {
        // Arrange: node_id_1 and node_id_2 swapped on the wire
        var records = Announcement().Stream.Records
                                    .Select(r => r.Type switch
                                    {
                                        Tlvs.NodeId1 => new PureTlvRecord(Tlvs.NodeId1, s_node2),
                                        Tlvs.NodeId2 => new PureTlvRecord(Tlvs.NodeId2, s_node1),
                                        _ => r
                                    });
        var swapped = ChannelAnnouncement2Payload.FromStream(new PureTlvStream(records));

        // Act
        var result = GossipV2Validator.ValidateChannelAnnouncement2(swapped, s_context);

        // Assert
        Assert.Equal(GossipValidationOutcome.Warn, result.Outcome);
        Assert.Equal(GossipRejectReason.NodeIdsNotOrdered, result.Reason);
    }

    [Fact]
    public void Given_AnotherChain_When_Validating_Then_Ignored()
    {
        // Act: an announcement without chain_hash is mainnet's (the draft's default)
        var result = GossipV2Validator.ValidateChannelAnnouncement2(Announcement(BitcoinNetwork.Mainnet.ChainHash),
                                                                    s_context);

        // Assert
        Assert.Equal(GossipRejectReason.UnknownChain, result.Reason);
    }

    [Fact]
    public void Given_AFundingBlockBelowTheDepth_When_Validating_Then_Ignored()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelAnnouncement2(
            Announcement(scid: new ShortChannelId(Tip - 3, 1, 1)), s_context);

        // Assert
        Assert.Equal(GossipRejectReason.InsufficientDepth, result.Reason);
    }

    [Fact]
    public void Given_ABlacklistedNode_When_Validating_Then_Ignored()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelAnnouncement2(
            Announcement(), s_context with { IsBlacklisted = k => k == s_node2 });

        // Assert
        Assert.Equal(GossipRejectReason.BlacklistedNode, result.Reason);
    }

    [Fact]
    public void Given_TheSameAnnouncement2Known_When_Validating_Then_AlreadyKnown()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelAnnouncement2(Announcement(), s_context, V2Channel());

        // Assert
        Assert.Equal(GossipRejectReason.AlreadyKnown, result.Reason);
    }

    [Fact]
    public void Given_AV1ChannelOfTheSameNodes_When_Validating_Then_AcceptedToBeAddedToIt()
    {
        // Arrange
        var v1 = new GraphChannel(s_scid, s_node1, s_node2, Key(0x02, 3), Key(0x02, 4), 1_000_000);

        // Act
        var result = GossipV2Validator.ValidateChannelAnnouncement2(Announcement(), s_context, v1);

        // Assert
        Assert.Equal(GossipValidationOutcome.Accept, result.Outcome);
    }

    [Fact]
    public void Given_AV1ChannelOfOtherNodes_When_ValidatedWithItsSignature_Then_AConflictThatMayBlacklist()
    {
        // Arrange
        var v1 = new GraphChannel(s_scid, Key(0x02, 7), s_node2, Key(0x02, 3), Key(0x02, 4), 1_000_000);

        // Act
        var unverified = GossipV2Validator.ValidateChannelAnnouncement2(Announcement(), s_context, v1);
        var verified = GossipV2Validator.ValidateChannelAnnouncement2(Announcement(), s_context, v1,
                                                                      signatureVerified: true);

        // Assert
        Assert.Equal(GossipRejectReason.ConflictingAnnouncement, unverified.Reason);
        Assert.False(unverified.MayBlacklist);
        Assert.True(verified.MayBlacklist);
    }

    [Fact]
    public void Given_UnknownEvenFeatures_When_Validating_Then_AcceptedButNotRoutable()
    {
        // Arrange: bit 100 (even, unknown)
        var features = new byte[13];
        features[0] = 0x10;

        // Act
        var result = GossipV2Validator.ValidateChannelAnnouncement2(Announcement(features: features), s_context);

        // Assert
        Assert.Equal(GossipValidationOutcome.Accept, result.Outcome);
        Assert.False(result.Routable);
    }

    [Fact]
    public void Given_AnUpdateForAnUnknownChannel_When_Validating_Then_IgnoredAsUnknown()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelUpdate2(Update(Tip), s_context, null);

        // Assert
        Assert.Equal(GossipRejectReason.UnknownChannel, result.Reason);
    }

    [Fact]
    public void Given_AnUpdateForOurOwnUnannouncedChannel_When_Validating_Then_AcceptedButNotForwardable()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelUpdate2(Update(Tip), s_context, null, isOwnChannel: true);

        // Assert
        Assert.Equal(GossipValidationOutcome.Accept, result.Outcome);
        Assert.False(result.Forwardable);
    }

    [Theory]
    [InlineData(Tip, true)]
    [InlineData(Tip + 1, true)]
    [InlineData(Tip + 2, false)]
    [InlineData(Tip - 2016, true)]
    [InlineData(Tip - 2017, false)]
    [InlineData(899u, false)]
    public void Given_AnUpdateBlockHeight_When_Validating_Then_OnlyTheBackdateWindowIsAccepted(uint blockHeight,
                                                                                            bool accepted)
    {
        // Act: [tip - max_backdate_blocks, tip] with a block of slack, never below the funding block (900)
        var result = GossipV2Validator.ValidateChannelUpdate2(Update(blockHeight), s_context, V2Channel());

        // Assert
        Assert.Equal(accepted, result.IsAccepted);
    }

    [Fact]
    public void Given_AnUnknownTip_When_ValidatingAnOldUpdate_Then_TheTipChecksAreSkipped()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelUpdate2(Update(1_000), s_context with { TipHeight = null },
                                                              V2Channel());

        // Assert
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Given_ASpentChannel_When_ValidatingAnEnablingUpdate_Then_IgnoredButADisablingOneIsAccepted()
    {
        // Arrange
        var spent = V2Channel(spentAt: Tip - 1);

        // Act
        var enabling = GossipV2Validator.ValidateChannelUpdate2(Update(Tip), s_context, spent);
        var disabling = GossipV2Validator.ValidateChannelUpdate2(Update(Tip, disableFlags: 0b001), s_context, spent);

        // Assert
        Assert.Equal(GossipRejectReason.ChannelSpent, enabling.Reason);
        Assert.True(disabling.IsAccepted);
    }

    [Fact]
    public void Given_AStoredV2Policy_When_ValidatingTheSameHeight_Then_ADuplicateOrAConflict()
    {
        // Arrange
        var stored = Update(Tip - 5);
        var channel = V2Channel(GraphPolicy.FromChannelUpdate2(stored, 1_000_000_000) with
        {
            RawUpdate = stored.GetBytes()
        });

        // Act
        var duplicate = GossipV2Validator.ValidateChannelUpdate2(Update(Tip - 5), s_context, channel);
        var conflict = GossipV2Validator.ValidateChannelUpdate2(Update(Tip - 5, feeBase: 9), s_context, channel,
                                                                signatureVerified: true);
        var older = GossipV2Validator.ValidateChannelUpdate2(Update(Tip - 6), s_context, channel);
        var newer = GossipV2Validator.ValidateChannelUpdate2(Update(Tip - 4), s_context, channel);

        // Assert
        Assert.Equal(GossipRejectReason.DuplicateUpdate, duplicate.Reason);
        Assert.Equal(GossipRejectReason.ConflictingSameTimestamp, conflict.Reason);
        Assert.True(conflict.MayBlacklist);
        Assert.Equal(GossipRejectReason.OutdatedUpdate, older.Reason);
        Assert.True(newer.IsAccepted);
    }

    [Fact]
    public void Given_AV1PolicyOfTheSameDirection_When_ValidatingAV2Update_Then_ItIsNotComparedWithIt()
    {
        // Arrange: a v1 policy dated by UNIX time far above any block height
        var channel = V2Channel(new GraphPolicy(1_700_000_000, 1, 0, 40, 1, 100, 10, 10));

        // Act
        var result = GossipV2Validator.ValidateChannelUpdate2(Update(Tip), s_context, channel);

        // Assert
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Given_AMaximumAboveTheCapacity_When_Validating_Then_NotRoutable()
    {
        // Act
        var result = GossipV2Validator.ValidateChannelUpdate2(Update(Tip, htlcMaximumMsat: 1_000_000_001),
                                                              s_context, V2Channel(), signatureVerified: true);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.False(result.Routable);
        Assert.True(result.MayBlacklist);
    }

    [Fact]
    public void Given_ANodeAnnouncement2_When_Validating_Then_TheNodeMustBeKnownNewerAndInsideTheWindow()
    {
        // Arrange
        var announcement = NodeAnnouncement2Payload.Create(ReadOnlySpan<byte>.Empty, Tip - 10, s_node1,
                                                           ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, []);
        var stale = NodeAnnouncement2Payload.Create(ReadOnlySpan<byte>.Empty, Tip - 2017, s_node1,
                                                    ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, []);
        var future = NodeAnnouncement2Payload.Create(ReadOnlySpan<byte>.Empty, Tip + 2, s_node1,
                                                     ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, []);

        // Act & Assert
        Assert.True(GossipV2Validator.ValidateNodeAnnouncement2(announcement, s_context, true, null).IsAccepted);
        Assert.Equal(GossipRejectReason.UnknownNode,
                     GossipV2Validator.ValidateNodeAnnouncement2(announcement, s_context, false, null).Reason);
        Assert.Equal(GossipRejectReason.NotNewer,
                     GossipV2Validator.ValidateNodeAnnouncement2(announcement, s_context, true, Tip - 10).Reason);
        Assert.Equal(GossipRejectReason.StaleUpdate,
                     GossipV2Validator.ValidateNodeAnnouncement2(stale, s_context, true, null).Reason);
        Assert.Equal(GossipRejectReason.TimestampTooFarInFuture,
                     GossipV2Validator.ValidateNodeAnnouncement2(future, s_context, true, null).Reason);
    }
}