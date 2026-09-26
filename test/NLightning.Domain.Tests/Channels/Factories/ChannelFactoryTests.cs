using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Channels.Factories;

using Domain.Bitcoin.Interfaces;
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

        // Act
        var channel = await channelFactory.CreateChannelV1AsNonInitiatorAsync(message, new FeatureOptions(),
                                                                              s_remoteNodeId);

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

        // Act
        var channel = await channelFactory.CreateChannelV1AsInitiatorAsync(request, new FeatureOptions(),
                                                                           s_remoteNodeId);

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

    private static ChannelFactory CreateNonInitiatorChannelFactory(NodeOptions? nodeOptions = null)
    {
        var signerMock = new Mock<ILightningSigner>();
        var basepoints = new ChannelBasepoints(s_remoteNodeId, s_remoteNodeId, s_remoteNodeId, s_remoteNodeId,
                                               s_remoteNodeId);
        var firstPerCommitmentPoint = s_remoteNodeId;
        signerMock.Setup(s => s.CreateNewChannel(out basepoints, out firstPerCommitmentPoint)).Returns(0);

        var feeServiceMock = new Mock<IFeeService>();
        feeServiceMock.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(LightningMoney.Satoshis(1_000));

        return new ChannelFactory(new Mock<IChannelIdFactory>().Object, new Mock<IChannelOpenValidator>().Object,
                                  feeServiceMock.Object, signerMock.Object,
                                  nodeOptions ?? new NodeOptions { MinimumChannelSize = LightningMoney.Satoshis(1_000) },
                                  new FakeSha256());
    }

    private static OpenChannel1Message CreateOpenChannel1Message(ChannelTypeTlv? channelTypeTlv,
                                                                 LightningMoney? openerDustLimit = null,
                                                                 LightningMoney? openerReserve = null,
                                                                 ChannelFlag channelFlags = ChannelFlag.None)
    {
        var payload = new OpenChannel1Payload(BitcoinNetwork.Mainnet.ChainHash, new ChannelFlags(channelFlags),
                                              s_temporaryChannelId, openerReserve ?? LightningMoney.Satoshis(1_000),
                                              s_remoteNodeId, openerDustLimit ?? LightningMoney.Satoshis(354),
                                              LightningMoney.Satoshis(1_000),
                                              s_remoteNodeId, LightningMoney.Satoshis(100_000), s_remoteNodeId,
                                              s_remoteNodeId, LightningMoney.Satoshis(1), 30,
                                              LightningMoney.Satoshis(100_000), s_remoteNodeId, LightningMoney.Zero,
                                              s_remoteNodeId, 144);

        return new OpenChannel1Message(payload, channelTypeTlv);
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