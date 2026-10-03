using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Channels.Factories;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

public class ChannelFactoryTests
{
    private static readonly FeatureOptions s_noSplice = new() { OptionSplice = FeatureSupport.No };

    private static readonly CompactPubKey s_remoteNodeId =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly ChannelId s_temporaryChannelId =
        Convert.FromHexString("0101010101010101010101010101010101010101010101010101010101010101");

    private readonly ChannelFactory _channelFactory =
        new(new Mock<IChannelIdFactory>().Object, new Mock<IChannelOpenValidator>().Object,
            new Mock<IFeeService>().Object, new Mock<ILightningSigner>().Object,
            new NodeOptions { MinimumChannelSize = LightningMoney.Satoshis(1_000) }, new Mock<ISha256>().Object);

    [Fact]
    public async Task Given_AnchorsAndFundingBelowAnchorFeePlusReserve_When_CreatingChannelAsInitiator_Then_Throws()
    {
        // Arrange
        // 1124 * 10000 / 1000 = 11240 sat fee + 2 * 330 sat anchors + 1000 sat reserve = 12900 sat
        var request = CreateRequest(LightningMoney.Satoshis(12_899));
        var negotiatedFeatures = new FeatureOptions { OptionAnchors = FeatureSupport.Optional };

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => _channelFactory.CreateChannelV1AsInitiatorAsync(request, negotiatedFeatures,
                                                                                  s_remoteNodeId));

        // Assert
        Assert.Contains("too small to cover fees", exception.Message);
    }

    [Fact]
    public async Task Given_NoAnchorsAndFundingCoveringNoAnchorFee_When_CreatingChannelAsInitiator_Then_FeeCheckPasses()
    {
        // Arrange
        // 724 * 10000 / 1000 = 7240 sat fee + 1000 sat reserve = 8240 sat
        var request = CreateRequest(LightningMoney.Satoshis(8_240));
        var negotiatedFeatures = new FeatureOptions { OptionAnchors = FeatureSupport.No };

        // Act
        var exception = await Record.ExceptionAsync(
                            () => _channelFactory.CreateChannelV1AsInitiatorAsync(request, negotiatedFeatures,
                                                                                  s_remoteNodeId));

        // Assert (later steps may fail on the bare mocks; only the fee check matters here)
        Assert.False(exception is ChannelErrorException && exception.Message.Contains("too small to cover fees"),
                     exception?.Message);
    }

    [Fact]
    public async Task Given_PushAmountAboveFundingAmount_When_CreatingChannelAsInitiator_Then_ThrowsChannelError()
    {
        // Arrange
        var request = CreateRequest(LightningMoney.Satoshis(100_000), LightningMoney.MilliSatoshis(100_000_001UL));
        var negotiatedFeatures = new FeatureOptions { OptionAnchors = FeatureSupport.No };

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => _channelFactory.CreateChannelV1AsInitiatorAsync(request, negotiatedFeatures,
                                                                                  s_remoteNodeId));

        // Assert
        Assert.Contains("Push amount is too large", exception.Message);
    }

    [Fact]
    public async Task Given_PushLeavingLessThanFee_When_CreatingChannelAsInitiator_Then_Throws()
    {
        // Arrange
        // 1124 * 10000 / 1000 = 11240 sat fee + 2 * 330 sat anchors = 11900 sat must stay with us
        var request = CreateRequest(LightningMoney.Satoshis(100_000), LightningMoney.Satoshis(88_101));
        var negotiatedFeatures = new FeatureOptions { OptionAnchors = FeatureSupport.Optional };

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => _channelFactory.CreateChannelV1AsInitiatorAsync(request, negotiatedFeatures,
                                                                                  s_remoteNodeId));

        // Assert
        Assert.Contains("Funder amount is too small to cover fees", exception.Message);
    }

    [Fact]
    public async Task Given_PushLeavingExactlyFee_When_CreatingChannelAsInitiator_Then_FeeCheckPasses()
    {
        // Arrange
        var request = CreateRequest(LightningMoney.Satoshis(100_000), LightningMoney.Satoshis(88_100));
        var negotiatedFeatures = new FeatureOptions { OptionAnchors = FeatureSupport.Optional };

        // Act
        var exception = await Record.ExceptionAsync(
                            () => _channelFactory.CreateChannelV1AsInitiatorAsync(request, negotiatedFeatures,
                                                                                  s_remoteNodeId));

        // Assert (later steps may fail on the bare mocks; only the push and fee checks matter here)
        Assert.False(exception is ChannelErrorException && (exception.Message.Contains("to cover fees")
                                                         || exception.Message.Contains("Push amount")),
                     exception?.Message);
    }

    [Fact]
    public async Task
        Given_ChannelTypeWithoutUpfrontShutdownScriptAndOptionNotNegotiated_When_CreatingChannelAsNonInitiator_Then_ChannelIsCreated()
    {
        // Arrange (BOLT 2: upfront_shutdown_script is only required when option_upfront_shutdown_script is negotiated)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var negotiatedFeatures = new FeatureOptions { UpfrontShutdownScript = FeatureSupport.No };

        // Act
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, negotiatedFeatures,
                                                                              s_remoteNodeId);

        // Assert
        Assert.Equal(s_temporaryChannelId, channel.ChannelId);
        Assert.Equal(ChannelState.V1Opening, channel.State);
    }

    [Theory]
    [InlineData("5120" + "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", FeatureSupport.No)]
    [InlineData("76a914" + "cccccccccccccccccccccccccccccccccccccccc" + "88ac", FeatureSupport.Optional)]
    [InlineData("6a06" + "000000000000", FeatureSupport.Optional)]
    public async Task Given_UpfrontShutdownScriptOfAForbiddenForm_When_CreatingChannelAsNonInitiator_Then_ChannelError(
        string scriptHex, FeatureSupport anySegwit)
    {
        // Arrange (NL-776: CLN v26.06.8 sends a P2TR upfront script without option_shutdown_anysegwit)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()),
                                                upfrontShutdownScriptTlv: new UpfrontShutdownScriptTlv(
                                                    Convert.FromHexString(scriptHex)));
        var negotiatedFeatures = new FeatureOptions
        {
            UpfrontShutdownScript = FeatureSupport.Optional,
            BeyondSegwitShutdown = anySegwit
        };

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelFactory.CreateChannelV1AsNonInitiatorAsync(message, negotiatedFeatures,
                                                                                    s_remoteNodeId));

        // Assert
        Assert.Contains("upfront_shutdown_script", exception.Message);
        Assert.Equal(s_temporaryChannelId, exception.ChannelId);
    }

    [Theory]
    [InlineData("5120" + "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    [InlineData("0014" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    public async Task Given_UpfrontShutdownScriptAllowedWithAnySegwit_When_CreatingChannelAsNonInitiator_Then_ScriptIsKept(
        string scriptHex)
    {
        // Arrange
        var channelFactory = CreateNonInitiatorChannelFactory();
        var script = Convert.FromHexString(scriptHex);
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()),
                                                upfrontShutdownScriptTlv: new UpfrontShutdownScriptTlv(script));
        var negotiatedFeatures = new FeatureOptions
        {
            UpfrontShutdownScript = FeatureSupport.Optional,
            BeyondSegwitShutdown = FeatureSupport.Optional
        };

        // Act
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, negotiatedFeatures,
                                                                              s_remoteNodeId);

        // Assert
        if (script.Length == 0)
            Assert.Null(channel.RemoteUpfrontShutdownScript);
        else
            Assert.Equal(script, (byte[])channel.RemoteUpfrontShutdownScript!.Value);
    }

    [Theory]
    [InlineData(FeatureSupport.Optional)]
    [InlineData(FeatureSupport.Compulsory)]
    public async Task
        Given_OptionUpfrontShutdownScriptNegotiatedAndScriptMissing_When_CreatingChannelAsNonInitiator_Then_ChannelErrorCarriesTemporaryChannelId(
            FeatureSupport upfrontShutdownScript)
    {
        // Arrange
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var negotiatedFeatures = new FeatureOptions { UpfrontShutdownScript = upfrontShutdownScript };

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelFactory.CreateChannelV1AsNonInitiatorAsync(message, negotiatedFeatures,
                                                                                    s_remoteNodeId));

        // Assert
        Assert.Contains("Upfront shutdown script", exception.Message);
        Assert.Equal(s_temporaryChannelId, exception.ChannelId);
    }

    [Theory]
    [InlineData(FeatureSupport.Optional)]
    [InlineData(FeatureSupport.Compulsory)]
    public async Task Given_UpfrontShutdownScriptNegotiated_When_CreatingChannelAsInitiator_Then_ChannelIsCreatedWithoutScript(
        FeatureSupport upfrontShutdownScript)
    {
        // Arrange (NL-045: BOLT 2 allows a zero-length script, so a compulsory peer no longer refuses the open; the
        // caller sets a reserved wallet script before open_channel goes out)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var request = CreateRequest(LightningMoney.Satoshis(100_000));
        var negotiatedFeatures = new FeatureOptions { UpfrontShutdownScript = upfrontShutdownScript };

        // Act
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(request, negotiatedFeatures,
                                                                           s_remoteNodeId);

        // Assert
        Assert.Null(channel.LocalUpfrontShutdownScript);
    }

    [Fact]
    public async Task Given_OpeningChannel_When_LocalUpfrontShutdownScriptSet_Then_OnlyOurParamsCarryItOnce()
    {
        // Arrange (NL-045)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                              s_remoteNodeId);
        var before = channel.ChannelParams;
        BitcoinScript script = Convert.FromHexString("0014" + new string('c', 40));

        // Act
        channel.SetLocalUpfrontShutdownScript(script);

        // Assert: every other parameter is kept
        Assert.Equal(script, channel.LocalUpfrontShutdownScript);
        Assert.Equal(before.Local.WithUpfrontShutdownScript(script), channel.ChannelParams.Local);
        Assert.Equal(before.Remote, channel.ChannelParams.Remote);
        Assert.Equal(before.AnnounceChannel, channel.ChannelParams.AnnounceChannel);
        Assert.Equal(before.MinimumDepth, channel.ChannelParams.MinimumDepth);
        Assert.Throws<InvalidOperationException>(() => channel.SetLocalUpfrontShutdownScript(script));
    }

    [Fact]
    public async Task Given_ChannelPastOpening_When_LocalUpfrontShutdownScriptSet_Then_Throws()
    {
        // Arrange (NL-045: the script is announced in open_channel/accept_channel, never later)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                              s_remoteNodeId);
        channel.UpdateState(ChannelState.Open);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(
            () => channel.SetLocalUpfrontShutdownScript(Convert.FromHexString("0014" + new string('c', 40))));
        Assert.Null(channel.LocalUpfrontShutdownScript);
    }

    [Fact]
    public async Task Given_OurShutdownRecordedAtPeerHtlcIdThree_When_CheckingIncomingHtlcs_Then_IdsFromThreeAreAfterIt()
    {
        // Arrange (NL-279, B2-SHUT-S08)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                              s_remoteNodeId);
        Assert.False(channel.IsRemoteHtlcAddedAfterLocalShutdown(7));

        // Act
        channel.SetLocalShutdownScript(Convert.FromHexString("0014" + new string('a', 40)));
        channel.SetFirstRemoteHtlcIdAfterLocalShutdown(3);
        channel.SetFirstRemoteHtlcIdAfterLocalShutdown(5);

        // Assert: set once; 0-2 were added before our shutdown
        Assert.Equal(3UL, channel.FirstRemoteHtlcIdAfterLocalShutdown);
        Assert.False(channel.IsRemoteHtlcAddedAfterLocalShutdown(2));
        Assert.True(channel.IsRemoteHtlcAddedAfterLocalShutdown(3));
        Assert.True(channel.IsRemoteHtlcAddedAfterLocalShutdown(4));
    }

    [Fact]
    public async Task Given_OurShutdownWithoutRecordedBoundary_When_CheckingIncomingHtlcs_Then_NoneIsAfterIt()
    {
        // Arrange: a shutdown persisted by an older build keeps the old behavior
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                              s_remoteNodeId);

        // Act
        channel.SetLocalShutdownScript(Convert.FromHexString("0014" + new string('a', 40)));

        // Assert
        Assert.False(channel.IsRemoteHtlcAddedAfterLocalShutdown(0));
    }

    [Fact]
    public async Task Given_NewChannelAsNonInitiator_When_Created_Then_NextHtlcIdsZero()
    {
        // Arrange (BOLT 2: the first update_add_htlc sent by either side has id 0)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var negotiatedFeatures = new FeatureOptions { UpfrontShutdownScript = FeatureSupport.No };

        // Act
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, negotiatedFeatures,
                                                                              s_remoteNodeId);

        // Assert
        Assert.Equal(0UL, channel.LocalNextHtlcId);
        Assert.Equal(0UL, channel.RemoteNextHtlcId);
    }

    [Fact]
    public async Task Given_NewChannelAsInitiator_When_Created_Then_NextHtlcIdsZero()
    {
        // Arrange
        var channelFactory = CreateNonInitiatorChannelFactory();
        var request = CreateRequest(LightningMoney.Satoshis(100_000));
        var negotiatedFeatures = new FeatureOptions { OptionAnchors = FeatureSupport.No };

        // Act
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(request, negotiatedFeatures,
                                                                           s_remoteNodeId);

        // Assert
        Assert.Equal(0UL, channel.LocalNextHtlcId);
        Assert.Equal(0UL, channel.RemoteNextHtlcId);
    }

    [Fact]
    public async Task Given_Open_When_CreatingChannelAsNonInitiator_Then_LocalAndRemoteParamsMapped()
    {
        // Arrange: every value we announce differs from the opener's (NL-194)
        var nodeOptions = CreateDistinctNodeOptions();
        var channelFactory = CreateNonInitiatorChannelFactory(nodeOptions);
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));

        // Act: a peer without option_splice, so our in-flight limit is a share of the funding (NL-880)
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, s_noSplice, s_remoteNodeId);

        // Assert: the peer's values are what it sent
        var remote = channel.ChannelParams.Remote;
        Assert.Equal(LightningMoney.Satoshis(354), remote.DustLimitAmount);
        Assert.Equal(LightningMoney.Satoshis(1_000), remote.ChannelReserveAmount);
        Assert.Equal(LightningMoney.Satoshis(1), remote.HtlcMinimumAmount);
        Assert.Equal((ushort)30, remote.MaxAcceptedHtlcs);
        Assert.Equal(LightningMoney.Satoshis(100_000), remote.MaxHtlcValueInFlight);
        Assert.Equal((ushort)144, remote.ToSelfDelay);

        // Assert: ours come from our options, and our reserve is 1% of the funding
        var local = channel.ChannelParams.Local;
        Assert.Equal(nodeOptions.DustLimitAmount, local.DustLimitAmount);
        Assert.Equal(LightningMoney.Satoshis(1_000), local.ChannelReserveAmount);
        Assert.Equal(nodeOptions.HtlcMinimumAmount, local.HtlcMinimumAmount);
        Assert.Equal(nodeOptions.MaxAcceptedHtlcs, local.MaxAcceptedHtlcs);
        Assert.Equal(LightningMoney.Satoshis(50_000), local.MaxHtlcValueInFlight);
        Assert.Equal(nodeOptions.ToSelfDelay, local.ToSelfDelay);
    }

    [Fact]
    public async Task Given_OpenerDustAboveOnePercent_When_CreatingChannelAsNonInitiator_Then_OurReserveCoversTheirDust()
    {
        // Arrange: BOLT 2: the accepter MUST set channel_reserve_satoshis >= the opener's dust_limit_satoshis
        var channelFactory = CreateNonInitiatorChannelFactory(CreateDistinctNodeOptions());
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()),
                                                openerDustLimit: LightningMoney.Satoshis(1_500),
                                                openerReserve: LightningMoney.Satoshis(2_000));

        // Act
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                              s_remoteNodeId);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(1_500), channel.ChannelParams.Local.ChannelReserveAmount);
    }

    [Fact]
    public async Task Given_OpenerReserveBelowOurDust_When_CreatingChannelAsNonInitiator_Then_ChannelErrorIsThrown()
    {
        // Arrange: BOLT 2: the accepter MUST set dust_limit_satoshis <= the opener's channel_reserve_satoshis
        var channelFactory = CreateNonInitiatorChannelFactory(CreateDistinctNodeOptions());
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()),
                                                openerReserve: LightningMoney.Satoshis(399));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                                    s_remoteNodeId));

        // Assert
        Assert.Equal(s_temporaryChannelId, exception.ChannelId);
    }

    [Fact]
    public async Task Given_ChannelTypeWithoutAnchors_When_AnchorsNegotiated_Then_ChannelHasNoAnchors()
    {
        // Arrange: the channel type decides anchors, not the init features
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
        var negotiatedFeatures = new FeatureOptions { OptionAnchors = FeatureSupport.Optional };

        // Act
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, negotiatedFeatures,
                                                                              s_remoteNodeId);

        // Assert
        Assert.False(channel.ChannelParams.OptionAnchorOutputs);
    }

    [Fact]
    public async Task Given_Request_When_CreatingChannelAsInitiator_Then_LocalParamsMappedAndRemoteUnknown()
    {
        // Arrange
        var nodeOptions = CreateDistinctNodeOptions();
        var channelFactory = CreateNonInitiatorChannelFactory(nodeOptions);
        var request = CreateRequest(LightningMoney.Satoshis(200_000));
        request.ToSelfDelay = 300;

        // Act: a peer without option_splice, so our in-flight limit is a share of the funding (NL-880)
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(request, s_noSplice, s_remoteNodeId);

        // Assert
        var local = channel.ChannelParams.Local;
        Assert.Equal(nodeOptions.DustLimitAmount, local.DustLimitAmount);
        Assert.Equal(LightningMoney.Satoshis(2_000), local.ChannelReserveAmount);
        Assert.Equal(nodeOptions.HtlcMinimumAmount, local.HtlcMinimumAmount);
        Assert.Equal(nodeOptions.MaxAcceptedHtlcs, local.MaxAcceptedHtlcs);
        Assert.Equal(LightningMoney.Satoshis(100_000), local.MaxHtlcValueInFlight);
        Assert.Equal((ushort)300, local.ToSelfDelay);
        Assert.Equal(ChannelParty.Unknown, channel.ChannelParams.Remote);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_OpenChannelFlags_When_CreatingChannelAsNonInitiator_Then_TheAnnounceFlagIsStored(
        bool announce)
    {
        // Arrange (NL-341, G1-T1: the opener's announce_channel bit is kept with the channel)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var message = CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType()),
                                                channelFlags: announce ? ChannelFlag.AnnounceChannel
                                                                       : ChannelFlag.None);

        // Act
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                              s_remoteNodeId);

        // Assert
        Assert.Equal(announce, channel.AnnounceChannel);
        Assert.Equal(announce, channel.ChannelParams.WithRemote(channel.ChannelParams.Remote).AnnounceChannel);
    }

    [Theory]
    [InlineData(FeatureSupport.No)]
    [InlineData(FeatureSupport.Optional)]
    public async Task Given_PublicRequest_When_CreatingChannelAsInitiator_Then_AnnouncedWithoutScidAliasInTheType(
        FeatureSupport scidAlias)
    {
        // Arrange (BOLT 2: announce_channel MUST NOT be used with option_scid_alias in the channel type)
        var channelFactory = CreateNonInitiatorChannelFactory();
        var request = CreateRequest(LightningMoney.Satoshis(200_000));
        request.IsPublic = true;

        // Act
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(
                          request, new FeatureOptions { ScidAlias = scidAlias }, s_remoteNodeId);

        // Assert
        Assert.True(channel.AnnounceChannel);
        Assert.False(channel.ChannelParams.ToChannelType().IsFeatureSet(Feature.OptionScidAlias, true));
        Assert.Equal(scidAlias == FeatureSupport.No ? FeatureSupport.No : FeatureSupport.Optional,
                     channel.ChannelParams.UseScidAlias);
    }

    [Fact]
    public async Task Given_PrivateRequestWithScidAliasNegotiated_When_CreatingChannelAsInitiator_Then_TheTypeHasIt()
    {
        // Arrange
        var channelFactory = CreateNonInitiatorChannelFactory();
        var request = CreateRequest(LightningMoney.Satoshis(200_000));

        // Act
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(
                          request, new FeatureOptions { ScidAlias = FeatureSupport.Optional }, s_remoteNodeId);

        // Assert
        Assert.False(channel.AnnounceChannel);
        Assert.True(channel.ChannelParams.ToChannelType().IsFeatureSet(Feature.OptionScidAlias, true));
    }

    [Fact]
    public async Task Given_PublicZeroConfRequest_When_CreatingChannelAsInitiator_Then_ChannelErrorIsThrown()
    {
        // Arrange (a zero-conf channel has no confirmed short channel id to announce)
        var channelFactory = CreateNonInitiatorChannelFactory(new NodeOptions
        {
            MinimumChannelSize = LightningMoney.Satoshis(1_000),
            Features = new FeatureOptions { ZeroConf = FeatureSupport.Optional }
        });
        var request = CreateRequest(LightningMoney.Satoshis(200_000));
        request.IsPublic = true;
        request.IsZeroConfChannel = true;

        // Act / Assert
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelFactory.CreateChannelV1AsInitiatorAsync(
                                request, new FeatureOptions { ZeroConf = FeatureSupport.Optional }, s_remoteNodeId));
        Assert.Contains("public", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_SpliceNegotiated_When_CreatingChannel_Then_InFlightLimitHasNoCap(bool isInitiator)
    {
        // Arrange: max_htlc_value_in_flight_msat is fixed for the channel's lifetime (BOLT 2) while a splice can grow
        // the channel, so a share of the opening capacity would cap the peer's sends at the first size (NL-880)
        var channelFactory = CreateNonInitiatorChannelFactory(CreateDistinctNodeOptions());
        var withSplice = new FeatureOptions { OptionAnchors = FeatureSupport.No, OptionSplice = FeatureSupport.Optional };

        // Act
        var channel = isInitiator
                          ? await channelFactory.CreateChannelV1AsInitiatorAsync(
                                CreateRequest(LightningMoney.Satoshis(200_000)), withSplice, s_remoteNodeId)
                          : await channelFactory.CreateChannelV1AsNonInitiatorAsync(
                                CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType())),
                                withSplice, s_remoteNodeId);

        // Assert
        Assert.Equal(ulong.MaxValue, channel.ChannelParams.Local.MaxHtlcValueInFlight.MilliSatoshi);
    }

    [Theory]
    [InlineData(true, 100_000)]
    [InlineData(false, 50_000)]
    public async Task Given_LimitOnSpliceableChannels_When_SpliceNegotiated_Then_InFlightLimitIsAShareOfTheFunding(
        bool isInitiator, long expectedSatoshis)
    {
        // Arrange: the operator keeps the percentage on channels that can be spliced (Node:LimitInFlightOnSpliceable...)
        var nodeOptions = CreateDistinctNodeOptions();
        nodeOptions.LimitInFlightOnSpliceableChannels = true;
        var channelFactory = CreateNonInitiatorChannelFactory(nodeOptions);
        var withSplice = new FeatureOptions { OptionAnchors = FeatureSupport.No, OptionSplice = FeatureSupport.Optional };

        // Act
        var channel = isInitiator
                          ? await channelFactory.CreateChannelV1AsInitiatorAsync(
                                CreateRequest(LightningMoney.Satoshis(200_000)), withSplice, s_remoteNodeId)
                          : await channelFactory.CreateChannelV1AsNonInitiatorAsync(
                                CreateOpenChannel1Message(new ChannelTypeTlv(FeatureSet.NewBasicChannelType())),
                                withSplice, s_remoteNodeId);

        // Assert: 50 % of the funding
        Assert.Equal(LightningMoney.Satoshis(expectedSatoshis), channel.ChannelParams.Local.MaxHtlcValueInFlight);
    }

    private static NodeOptions CreateDistinctNodeOptions()
    {
        return new NodeOptions
        {
            MinimumChannelSize = LightningMoney.Satoshis(1_000),
            DustLimitAmount = LightningMoney.Satoshis(400),
            HtlcMinimumAmount = LightningMoney.Satoshis(2),
            MaxAcceptedHtlcs = 20,
            ToSelfDelay = 200,
            AllowUpToPercentageOfChannelFundsInFlight = 50
        };
    }

    [Theory]
    [InlineData(253, 275)] // 1 sat/vB floored at BOLT 3's 253: an LDK peer asked for 254 (NL-564)
    [InlineData(275, 275)]
    [InlineData(2_500, 2_500)]
    public async Task Given_AnEstimate_When_CreatingChannelAsInitiatorWithoutAFeerate_Then_TheFloorApplies(
        long estimate, long expected)
    {
        // Arrange
        var channelFactory = CreateNonInitiatorChannelFactory(estimatePerKw: LightningMoney.Satoshis(estimate));
        var request = CreateRequest(LightningMoney.Satoshis(100_000));
        request.FeeRatePerKw = null;

        // Act
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(request, new FeatureOptions(),
                                                                           s_remoteNodeId);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(expected), channel.ChannelParams.FeeRateAmountPerKw);
    }

    [Fact]
    public async Task Given_ARequestFeerate_When_CreatingChannelAsInitiator_Then_ItIsUsedAsGiven()
    {
        // Arrange (an explicit feerate is the operator's choice, at least ChannelConstants.MinFeePerKw; only the
        // estimate is floored)
        var channelFactory = CreateNonInitiatorChannelFactory(estimatePerKw: LightningMoney.Satoshis(5_000));
        var request = CreateRequest(LightningMoney.Satoshis(100_000));
        request.FeeRatePerKw = LightningMoney.Satoshis(1_000);

        // Act
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(request, new FeatureOptions(),
                                                                           s_remoteNodeId);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(1_000), channel.ChannelParams.FeeRateAmountPerKw);
    }

    private static ChannelFactory CreateNonInitiatorChannelFactory(NodeOptions? nodeOptions = null,
                                                                   LightningMoney? estimatePerKw = null)
    {
        var signerMock = new Mock<ILightningSigner>();
        var basepoints = new ChannelBasepoints(s_remoteNodeId, s_remoteNodeId, s_remoteNodeId, s_remoteNodeId,
                                               s_remoteNodeId);
        var firstPerCommitmentPoint = s_remoteNodeId;
        signerMock.Setup(s => s.CreateNewChannel(out basepoints, out firstPerCommitmentPoint)).Returns(0);

        var feeServiceMock = new Mock<IFeeService>();
        feeServiceMock.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(estimatePerKw ?? LightningMoney.Satoshis(1_000));

        return new ChannelFactory(new Mock<IChannelIdFactory>().Object, new Mock<IChannelOpenValidator>().Object,
                                  feeServiceMock.Object, signerMock.Object,
                                  nodeOptions ?? new NodeOptions { MinimumChannelSize = LightningMoney.Satoshis(1_000) },
                                  new FakeSha256());
    }

    private static OpenChannel1Message CreateOpenChannel1Message(ChannelTypeTlv? channelTypeTlv,
                                                                 LightningMoney? openerDustLimit = null,
                                                                 LightningMoney? openerReserve = null,
                                                                 ChannelFlag channelFlags = ChannelFlag.None,
                                                                 UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv =
                                                                     null)
    {
        var payload = new OpenChannel1Payload(BitcoinNetwork.Mainnet.ChainHash, new ChannelFlags(channelFlags),
                                              s_temporaryChannelId, openerReserve ?? LightningMoney.Satoshis(1_000),
                                              s_remoteNodeId, openerDustLimit ?? LightningMoney.Satoshis(354),
                                              LightningMoney.Satoshis(1_000),
                                              s_remoteNodeId, LightningMoney.Satoshis(100_000), s_remoteNodeId,
                                              s_remoteNodeId, LightningMoney.Satoshis(1), 30,
                                              LightningMoney.Satoshis(100_000), s_remoteNodeId, LightningMoney.Zero,
                                              s_remoteNodeId, 144);

        return new OpenChannel1Message(payload, channelTypeTlv, upfrontShutdownScriptTlv);
    }

    private static OpenChannelClientRequest CreateRequest(LightningMoney fundingAmount,
                                                          LightningMoney? pushAmount = null)
    {
        return new OpenChannelClientRequest("node", fundingAmount)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000),
            ChannelReserveAmount = LightningMoney.Satoshis(1_000),
            PushAmount = pushAmount
        };
    }
}