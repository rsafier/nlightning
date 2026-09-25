using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;

public class FundingConfirmedMessageHandlerTests
{
    private static readonly CompactPubKey s_emptyPubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly Mock<ILightningSigner> _mockLightningSigner = new();
    private readonly Mock<IMessageFactory> _mockMessageFactory = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly FundingConfirmedMessageHandler _handler;

    public FundingConfirmedMessageHandlerTests()
    {
        _mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);
        _handler = new FundingConfirmedMessageHandler(new Mock<IChannelMemoryRepository>().Object,
                                                      _mockLightningSigner.Object,
                                                      new Mock<ILogger<FundingConfirmedMessageHandler>>().Object,
                                                      _mockMessageFactory.Object, _mockUnitOfWork.Object);
    }

    [Theory]
    [InlineData(ChannelState.ReadyForUs)]
    [InlineData(ChannelState.Open)]
    public async Task Given_ChannelAlreadyConfirmedForUs_When_HandleAsync_Then_NothingChangesAndNoMessageIsSent(
        ChannelState state)
    {
        // Arrange
        var channel = CreateChannel(state);
        var commitmentNumberBefore = channel.CommitmentNumber!.Value;
        var messagesSent = 0;
        _handler.OnMessageReady += (_, _) => messagesSent++;

        // Act
        await _handler.HandleAsync(channel);

        // Assert
        Assert.Equal(state, channel.State);
        Assert.Equal(commitmentNumberBefore, channel.CommitmentNumber.Value);
        Assert.Equal(0, messagesSent);
        _mockLightningSigner.Verify(s => s.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>()),
                                    Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task Given_FundingSignedChannel_When_HandleAsync_Then_ChannelIsReadyForUsAndChannelReadyIsSent()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned);
        var commitmentNumberBefore = channel.CommitmentNumber!.Value;
        var messagesSent = 0;
        _handler.OnMessageReady += (_, _) => messagesSent++;

        // Act
        await _handler.HandleAsync(channel);

        // Assert
        Assert.Equal(ChannelState.ReadyForUs, channel.State);
        Assert.Equal(commitmentNumberBefore + 1, channel.CommitmentNumber.Value);
        Assert.Equal(1, messagesSent);
    }

    private static ChannelModel CreateChannel(ChannelState state)
    {
        var fundingAmount = LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(fundingAmount, s_emptyPubKey, s_emptyPubKey)
        {
            TransactionId = TxId.One,
            Index = 0
        };
        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_emptyPubKey, s_emptyPubKey, s_emptyPubKey, s_emptyPubKey,
                                            s_emptyPubKey, s_emptyPubKey);
        var commitmentNumber = new CommitmentNumber(s_emptyPubKey, s_emptyPubKey, new FakeSha256());

        return new ChannelModel(channelConfig, ChannelId.Zero, commitmentNumber, fundingOutput, false, null, null,
                                LightningMoney.Zero, keySet, 0, 0, fundingAmount, keySet, 0, s_emptyPubKey, 0, state,
                                ChannelVersion.V1);
    }
}