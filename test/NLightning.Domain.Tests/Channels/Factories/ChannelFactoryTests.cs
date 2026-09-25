namespace NLightning.Domain.Tests.Channels.Factories;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Factories;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;

public class ChannelFactoryTests
{
    private static readonly CompactPubKey s_remoteNodeId =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

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

    private static OpenChannelClientRequest CreateRequest(LightningMoney fundingAmount)
    {
        return new OpenChannelClientRequest("node", fundingAmount)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000),
            ChannelReserveAmount = LightningMoney.Satoshis(1_000)
        };
    }
}