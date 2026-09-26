namespace NLightning.Domain.Tests.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Validation;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// One test per BOLT 7 receiver rule the pure validator implements (B7-CA-03/04, B7-NA-03/04, B7-CU-02/03,
/// B7-PR-02).
/// </summary>
public class GossipValidatorTests
{
    private const ulong Now = 1_700_000_000;
    private static readonly ChainHash s_chain = BitcoinNetwork.Regtest.ChainHash;
    private static readonly ChainHash s_otherChain = BitcoinNetwork.Mainnet.ChainHash;
    private static readonly ShortChannelId s_scid = new(1_000, 2, 1);
    private static readonly CompactPubKey s_node1 = Key(0x02, 1);
    private static readonly CompactPubKey s_node2 = Key(0x03, 2);

    private static readonly GossipValidationContext s_context = new(s_chain, Now, 1_005);

    #region channel_announcement

    [Fact]
    public void Given_ValidAnnouncement_When_Validating_Then_Accepted()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(Announcement(), s_context);

        // Assert
        Assert.Equal(GossipValidationOutcome.Accept, result.Outcome);
        Assert.True(result.Routable);
        Assert.True(result.Forwardable);
    }

    [Fact]
    public void Given_NodeIdsNotOrdered_When_Validating_Then_Warn()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(
            Announcement() with { NodeId1 = (byte[])s_node2, NodeId2 = (byte[])s_node1 }, s_context);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Warn, GossipRejectReason.NodeIdsNotOrdered, "B7-CA-03");
    }

    [Fact]
    public void Given_EqualNodeIds_When_Validating_Then_Warn()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(Announcement() with { NodeId2 = (byte[])s_node1 },
                                                                 s_context);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Warn, GossipRejectReason.NodeIdsNotOrdered, "B7-CA-03");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Given_MalformedKey_When_Validating_Then_Warn(int which)
    {
        // Arrange: prefix 0x04 (not compressed)
        var bad = new byte[33];
        bad[0] = 0x04;
        var fields = which switch
        {
            0 => Announcement() with { NodeId1 = bad },
            1 => Announcement() with { NodeId2 = bad },
            2 => Announcement() with { BitcoinKey1 = bad },
            _ => Announcement() with { BitcoinKey2 = new byte[32] }
        };

        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(fields, s_context);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Warn, GossipRejectReason.InvalidPublicKey, "B7-CA-03");
    }

    [Fact]
    public void Given_UnknownChain_When_Validating_Then_Ignored()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(Announcement() with { ChainHash = s_otherChain },
                                                                 s_context);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.UnknownChain, "B7-CA-03");
    }

    [Fact]
    public void Given_UnknownEvenFeature_When_Validating_Then_AcceptedButNotRoutable()
    {
        // Arrange: bit 100 (even, unknown)
        var features = new byte[13];
        features[0] = 0x10;

        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(Announcement() with { Features = features },
                                                                 s_context);

        // Assert
        Assert.Equal(GossipValidationOutcome.Accept, result.Outcome);
        Assert.False(result.Routable);
    }

    [Theory]
    [InlineData(1_005u, true)] // 6 confirmations
    [InlineData(1_004u, true)] // 5: within the default 1-block tolerance
    [InlineData(1_003u, false)] // 4
    [InlineData(999u, false)] // SCID above the tip
    public void Given_Depth_When_Validating_Then_NeedsSixConfirmationsWithTolerance(uint tip, bool accepted)
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(Announcement(), s_context with { TipHeight = tip });

        // Assert
        if (accepted)
            Assert.True(result.IsAccepted);
        else
            AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.InsufficientDepth, "B7-CA-03");
    }

    [Fact]
    public void Given_NoTolerance_When_FiveConfirmations_Then_Ignored()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(
            Announcement(), s_context with { TipHeight = 1_004, DepthToleranceBlocks = 0 });

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.InsufficientDepth, "B7-CA-03");
    }

    [Fact]
    public void Given_NoTip_When_Validating_Then_DepthLeftToTheChainStage()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(Announcement(), s_context with { TipHeight = null });

        // Assert
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Given_BlacklistedNode_When_Validating_Then_Ignored()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(
            Announcement(), s_context with { IsBlacklisted = key => key == s_node2 });

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.BlacklistedNode, "B7-CA-04");
    }

    [Fact]
    public void Given_SameAnnouncementKnown_When_Validating_Then_IgnoredAsKnown()
    {
        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(Announcement(), s_context, KnownChannel());

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.AlreadyKnown, "B7-CA-05");
        Assert.False(result.MayBlacklist);
    }

    [Fact]
    public void Given_DifferentAnnouncementForSameFunding_When_Validating_Then_IgnoredAndMayBlacklist()
    {
        // Arrange: same SCID, another bitcoin key
        var fields = Announcement() with { BitcoinKey2 = (byte[])Key(0x02, 99) };

        // Act
        var result = GossipValidator.ValidateChannelAnnouncement(fields, s_context, KnownChannel());

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.ConflictingAnnouncement,
                       "B7-CA-04");
        Assert.True(result.MayBlacklist);
    }

    #endregion

    #region node_announcement

    [Fact]
    public void Given_ValidNodeAnnouncement_When_Validating_Then_AcceptedWithAddresses()
    {
        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(NodeAnnouncement(), true, 10, out var addresses);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.True(result.Forwardable);
        Assert.Single(addresses!.Addresses);
    }

    [Fact]
    public void Given_InvalidNodeId_When_Validating_Then_Warn()
    {
        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(NodeAnnouncement() with { NodeId = new byte[33] },
                                                              true, null, out var addresses);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Warn, GossipRejectReason.InvalidPublicKey, "B7-NA-03");
        Assert.Null(addresses);
    }

    [Fact]
    public void Given_NodeWithoutChannels_When_Validating_Then_Ignored()
    {
        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(NodeAnnouncement(), false, null, out _);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.UnknownNode, "B7-NA-03");
    }

    [Theory]
    [InlineData(99u)]
    [InlineData(100u)]
    public void Given_NotNewerTimestamp_When_Validating_Then_Ignored(uint timestamp)
    {
        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(NodeAnnouncement() with { Timestamp = timestamp }, true,
                                                              100, out _);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.NotNewer, "B7-NA-03");
    }

    [Fact]
    public void Given_TruncatedAddresses_When_Validating_Then_Warn()
    {
        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(
            NodeAnnouncement() with { Addresses = Convert.FromHexString("01" + "7f00") }, true, null, out var list);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Warn, GossipRejectReason.MalformedAddresses, "B7-NA-03");
        Assert.True(list!.IsMalformed);
    }

    [Fact]
    public void Given_UnknownAddressTypeAndPortZero_When_Validating_Then_AcceptedWithTheUsableOnes()
    {
        // Arrange: port-0 IPv4, good IPv4, unknown type 9 and a trailing IPv4 that must be ignored
        var addresses = Convert.FromHexString("01" + "7f000001" + "0000"
                                            + "01" + "0a000001" + "2607"
                                            + "09" + "0102"
                                            + "01" + "0a000002" + "2607");

        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(NodeAnnouncement() with { Addresses = addresses },
                                                              true, null, out var list);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.Equal(["10.0.0.1"], list!.Addresses.Select(a => a.Host));
    }

    [Fact]
    public void Given_TwoDnsAddresses_When_Validating_Then_AcceptedButNotForwardable()
    {
        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(
            NodeAnnouncement() with
            {
                Addresses = Convert.FromHexString("05" + "01" + "61" + "2607" + "05" + "01" + "62" + "2607")
            }, true, null, out var list);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.False(result.Forwardable);
        Assert.Single(list!.Addresses);
    }

    [Fact]
    public void Given_UnknownEvenNodeFeature_When_Validating_Then_AcceptedButNotRoutable()
    {
        // Arrange: bit 100
        var features = new byte[13];
        features[0] = 0x10;

        // Act
        var result = GossipValidator.ValidateNodeAnnouncement(NodeAnnouncement() with { Features = features }, true,
                                                              null, out _);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.False(result.Routable);
        Assert.Equal("B7-NA-04", result.RequirementId);
    }

    #endregion

    #region channel_update

    [Fact]
    public void Given_ValidUpdate_When_Validating_Then_AcceptedAndForwardable()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(), s_context, KnownChannel());

        // Assert
        Assert.True(result.IsAccepted);
        Assert.True(result.Forwardable);
        Assert.True(result.Routable);
    }

    [Fact]
    public void Given_UpdateForUnknownChain_When_Validating_Then_Ignored()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(chain: s_otherChain), s_context, KnownChannel());

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.UnknownChain, "B7-CU-02");
    }

    [Fact]
    public void Given_UpdateWithoutAnnouncement_When_NotOurChannel_Then_IgnoredAsOrphan()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(), s_context, null);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.UnknownChannel, "B7-CU-02");
    }

    [Fact]
    public void Given_UpdateWithoutAnnouncement_When_OurChannel_Then_AcceptedButNotForwardable()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(), s_context, null, isOwnChannel: true);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.False(result.Forwardable);
    }

    [Fact]
    public void Given_SpentChannel_When_UpdateNotDisabled_Then_Ignored()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(), s_context,
                                                           KnownChannel().WithSpentAtHeight(1_010));

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.ChannelSpent, "B7-CU-02");
    }

    [Fact]
    public void Given_SpentChannel_When_UpdateDisabled_Then_AcceptedAndForwardable()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(channelFlags: 0b10), s_context,
                                                           KnownChannel().WithSpentAtHeight(1_010));

        // Assert
        Assert.True(result.IsAccepted);
        Assert.True(result.Forwardable);
    }

    [Fact]
    public void Given_SameTimestampSameFields_When_Validating_Then_IgnoredAsDuplicate()
    {
        // Arrange
        var stored = KnownChannel().WithPolicy(GraphPolicy.FromChannelUpdate(Update()));

        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(), s_context, stored);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.DuplicateUpdate, "B7-CU-02");
        Assert.False(result.MayBlacklist);
    }

    [Fact]
    public void Given_SameTimestampDifferentFields_When_Validating_Then_IgnoredAndMayBlacklist()
    {
        // Arrange
        var stored = KnownChannel().WithPolicy(GraphPolicy.FromChannelUpdate(Update()));

        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(feeBase: 2), s_context, stored);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.ConflictingSameTimestamp,
                       "B7-CU-02");
        Assert.True(result.MayBlacklist);
    }

    [Fact]
    public void Given_OlderTimestamp_When_Validating_Then_Ignored()
    {
        // Arrange
        var stored = KnownChannel().WithPolicy(GraphPolicy.FromChannelUpdate(Update()));

        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(timestamp: (uint)Now - 101), s_context, stored);

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.OutdatedUpdate, "B7-CU-02");
    }

    [Fact]
    public void Given_OtherDirectionStored_When_Validating_Then_ComparesOnlyItsOwnDirection()
    {
        // Arrange: a newer direction-1 policy does not make a direction-0 update outdated
        var stored = KnownChannel().WithPolicy(GraphPolicy.FromChannelUpdate(Update(channelFlags: 1)));

        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(timestamp: (uint)Now - 200), s_context, stored);

        // Assert
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Given_ExplicitLastPolicy_When_OwnPrivateChannel_Then_UsedForOrdering()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(), s_context, null, true,
                                                           GraphPolicy.FromChannelUpdate(Update(timestamp: (uint)Now)));

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.OutdatedUpdate, "B7-CU-02");
    }

    [Theory]
    [InlineData(14 * 86_400, true)]
    [InlineData(14 * 86_400 + 1, false)]
    public void Given_FutureTimestamp_When_Validating_Then_AtMostFourteenDaysAhead(int aheadSeconds, bool accepted)
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(timestamp: (uint)(Now + (ulong)aheadSeconds)),
                                                           s_context, KnownChannel());

        // Assert
        if (accepted)
            Assert.True(result.IsAccepted);
        else
            AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.TimestampTooFarInFuture,
                           "B7-CU-02");
    }

    [Fact]
    public void Given_StaleUpdate_When_Validating_Then_IgnoredUnlessDisabledByPolicy()
    {
        // Arrange: 1,209,601 s old
        var update = Update(timestamp: (uint)(Now - 1_209_601));

        // Act
        var result = GossipValidator.ValidateChannelUpdate(update, s_context, KnownChannel());
        var kept = GossipValidator.ValidateChannelUpdate(update, s_context with { IgnoreStaleUpdates = false },
                                                         KnownChannel());

        // Assert
        AssertRejected(result, GossipValidationOutcome.Ignore, GossipRejectReason.StaleUpdate, "B7-PR-02");
        Assert.True(kept.IsAccepted);
    }

    [Fact]
    public void Given_HtlcMaxBelowMin_When_Validating_Then_AcceptedButNotRoutable()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(htlcMin: 2_000, htlcMax: 1_999), s_context,
                                                           KnownChannel());

        // Assert
        Assert.True(result.IsAccepted);
        Assert.False(result.Routable);
        Assert.False(result.MayBlacklist);
        Assert.Equal("B7-CU-03", result.RequirementId);
    }

    [Fact]
    public void Given_HtlcMaxAboveCapacity_When_Validating_Then_NotRoutableAndMayBlacklist()
    {
        // Arrange: capacity 1,000,000 sat = 1,000,000,000 msat
        var result = GossipValidator.ValidateChannelUpdate(Update(htlcMax: 1_000_000_001), s_context,
                                                           KnownChannel());
        var atCapacity = GossipValidator.ValidateChannelUpdate(Update(htlcMax: 1_000_000_000), s_context,
                                                               KnownChannel());

        // Assert
        Assert.True(result.IsAccepted);
        Assert.False(result.Routable);
        Assert.True(result.MayBlacklist);
        Assert.True(atCapacity.Routable);
    }

    [Fact]
    public void Given_DontForward_When_Validating_Then_AcceptedButNotForwardable()
    {
        // Act
        var result = GossipValidator.ValidateChannelUpdate(Update(messageFlags: 0b11), s_context, KnownChannel());

        // Assert
        Assert.True(result.IsAccepted);
        Assert.False(result.Forwardable);
    }

    #endregion

    [Theory]
    [InlineData("02", true)]
    [InlineData("03", true)]
    [InlineData("04", false)]
    [InlineData("00", false)]
    public void Given_KeyPrefix_When_Checking_Then_OnlyCompressedPrefixesPass(string prefix, bool expected)
    {
        // Arrange
        var key = Convert.FromHexString(prefix + new string('1', 64));

        // Act / Assert
        Assert.Equal(expected, GossipValidator.IsCompressedPubKey(key));
        Assert.False(GossipValidator.IsCompressedPubKey(key.AsSpan(0, 32)));
    }

    private static void AssertRejected(GossipValidationResult result, GossipValidationOutcome outcome,
                                       GossipRejectReason reason, string requirementId)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(requirementId, result.RequirementId);
        Assert.False(result.IsAccepted);
        Assert.False(result.Forwardable);
    }

    private static CompactPubKey Key(byte prefix, byte last)
    {
        var bytes = new byte[33];
        bytes[0] = prefix;
        bytes[32] = last;
        return new CompactPubKey(bytes);
    }

    private static ChannelAnnouncementFields Announcement() =>
        new(s_chain, s_scid, (byte[])s_node1, (byte[])s_node2, (byte[])Key(0x02, 11), (byte[])Key(0x02, 12), ReadOnlyMemory<byte>.Empty);

    private static GraphChannel KnownChannel() =>
        new(s_scid, s_node1, s_node2, Key(0x02, 11), Key(0x02, 12), 1_000_000);

    private static NodeAnnouncementFields NodeAnnouncement() =>
        new((byte[])s_node1, 100, ReadOnlyMemory<byte>.Empty, Convert.FromHexString("01" + "7f000001" + "2607"));

    private static ChannelUpdatePayload Update(uint timestamp = (uint)Now - 100, byte messageFlags = 1,
                                               byte channelFlags = 0, ulong htlcMin = 1_000,
                                               ulong htlcMax = 100_000_000, uint feeBase = 1,
                                               ChainHash? chain = null) =>
        new(ChannelUpdatePayload.EmptySignature, chain ?? s_chain, s_scid, timestamp, messageFlags, channelFlags, 40,
            htlcMin, feeBase, 10, htlcMax);
}