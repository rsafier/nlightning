using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;

public class ChannelReadyMessageHandlerTests
{
    private static readonly CompactPubKey s_firstPoint = CreatePubKey(0x01);
    private static readonly CompactPubKey s_secondPoint = CreatePubKey(0x02);

    private readonly Mock<IChannelMemoryRepository> _mockChannelMemoryRepository = new();
    private readonly Mock<IChannelDbRepository> _mockChannelDbRepository = new();
    private readonly ChannelReadyMessageHandler _handler;

    public ChannelReadyMessageHandlerTests()
    {
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_mockChannelDbRepository.Object);
        _handler = new ChannelReadyMessageHandler(_mockChannelMemoryRepository.Object,
                                                  new Mock<ILogger<ChannelReadyMessageHandler>>().Object,
                                                  mockUnitOfWork.Object);
    }

    [Theory]
    [InlineData(ChannelState.V1FundingSigned)]
    [InlineData(ChannelState.ReadyForUs)]
    public async Task Given_FirstChannelReady_When_HandleAsync_Then_SecondPerCommitmentPointIsStored(
        ChannelState state)
    {
        // Arrange
        var channel = CreateChannel(state);
        SetupChannel(channel);
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, s_secondPoint));

        // Act
        await _handler.HandleAsync(message, state, new FeatureOptions(), channel.RemoteNodeId);

        // Assert
        Assert.Equal(s_secondPoint, channel.RemoteKeySet!.CurrentPerCommitmentCompactPoint);
        Assert.Equal(CryptoConstants.FirstPerCommitmentIndex - 1, channel.RemoteKeySet.CurrentPerCommitmentIndex);
    }

    [Fact]
    public async Task Given_RepeatedChannelReady_When_HandleAsync_Then_StoredPointIsNotAdvancedAgain()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned);
        SetupChannel(channel);
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, s_secondPoint));
        await _handler.HandleAsync(message, ChannelState.V1FundingSigned, new FeatureOptions(),
                                   channel.RemoteNodeId);
        var repeated = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, CreatePubKey(0x03)));

        // Act
        await _handler.HandleAsync(repeated, ChannelState.ReadyForThem, new FeatureOptions(), channel.RemoteNodeId);

        // Assert
        Assert.Equal(s_secondPoint, channel.RemoteKeySet!.CurrentPerCommitmentCompactPoint);
        Assert.Equal(CryptoConstants.FirstPerCommitmentIndex - 1, channel.RemoteKeySet.CurrentPerCommitmentIndex);
    }

    private void SetupChannel(ChannelModel channel)
    {
        _mockChannelMemoryRepository
           .Setup(r => r.TryGetChannel(channel.ChannelId, out It.Ref<ChannelModel>.IsAny!))
           .Callback((ChannelId _, out ChannelModel c) =>
            {
                c = channel;
            })
           .Returns(true);
        _mockChannelDbRepository.Setup(r => r.GetByIdAsync(channel.ChannelId)).ReturnsAsync(channel);
    }

    private static CompactPubKey CreatePubKey(byte last)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = last;
        return bytes;
    }

    private static ChannelModel CreateChannel(ChannelState state)
    {
        var emptyPubKey = CreatePubKey(0x00);
        var fundingAmount = LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(fundingAmount, emptyPubKey, emptyPubKey)
        {
            TransactionId = TxId.One,
            Index = 0
        };
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var localKeySet = new ChannelKeySetModel(0, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey,
                                                 emptyPubKey, emptyPubKey);
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey,
                                                              emptyPubKey, s_firstPoint);
        var commitmentNumber = new CommitmentNumber(emptyPubKey, emptyPubKey, new FakeSha256());

        return new ChannelModel(channelConfig, ChannelId.Zero, commitmentNumber, fundingOutput, false, null, null,
                                LightningMoney.Zero, localKeySet, 0, 0, fundingAmount, remoteKeySet, 0, emptyPubKey,
                                0, state, ChannelVersion.V1);
    }
}