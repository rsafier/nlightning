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

    private static ChannelFactory CreateNonInitiatorChannelFactory()
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
                                  new NodeOptions { MinimumChannelSize = LightningMoney.Satoshis(1_000) },
                                  new FakeSha256());
    }

    private static OpenChannel1Message CreateOpenChannel1Message(ChannelTypeTlv? channelTypeTlv)
    {
        var payload = new OpenChannel1Payload(BitcoinNetwork.Mainnet.ChainHash, new ChannelFlags((byte)0),
                                              s_temporaryChannelId, LightningMoney.Satoshis(1_000), s_remoteNodeId,
                                              LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
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