using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
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
    private readonly Mock<IChannelStateDbRepository> _mockChannelStateDbRepository = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly List<string> _calls = [];
    private readonly ChannelReadyMessageHandler _handler;

    public ChannelReadyMessageHandlerTests()
    {
        _mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_mockChannelDbRepository.Object);
        _mockUnitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(_mockChannelStateDbRepository.Object);
        _mockUnitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => _calls.Add("save")).Returns(Task.CompletedTask);
        _mockChannelStateDbRepository
           .Setup(r => r.InitializeAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelStateExtras?>()))
           .Callback(() => _calls.Add("initialize"))
           .Returns(Task.CompletedTask);
        _handler = new ChannelReadyMessageHandler(_mockChannelMemoryRepository.Object,
                                                  new Mock<ILogger<ChannelReadyMessageHandler>>().Object,
                                                  _mockUnitOfWork.Object);
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

    [Theory]
    [InlineData(ChannelState.V1FundingSigned)]
    [InlineData(ChannelState.ReadyForUs)]
    public async Task Given_FirstChannelReady_When_HandleAsync_Then_FirstSnapshotIsSavedWithBothRemotePoints(
        ChannelState state)
    {
        // Arrange - NL-232: the point of the peer's current commitment must be kept before channel_ready replaces it
        var channel = CreateChannel(state);
        SetupChannel(channel);
        ChannelCommitments? staged = null;
        _mockChannelStateDbRepository
           .Setup(r => r.InitializeAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelStateExtras?>()))
           .Callback((ChannelCommitments snapshot, ChannelStateExtras? _) =>
            {
                staged = snapshot;
                _calls.Add("initialize");
            })
           .Returns(Task.CompletedTask);
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, s_secondPoint));

        // Act
        await _handler.HandleAsync(message, state, new FeatureOptions(), channel.RemoteNodeId);

        // Assert
        Assert.NotNull(staged);
        Assert.Equal(["initialize", "save"], _calls);
        Assert.Same(staged, channel.Commitments);
        Assert.Equal(s_firstPoint, staged.RemoteCommit.PerCommitmentPoint);
        Assert.Equal(s_secondPoint, staged.RemoteNextPerCommitmentPoint);
        Assert.Equal(0UL, staged.LocalCommit.Number);
        Assert.Equal(0UL, staged.RemoteCommit.Number);
        Assert.Equal(channel.LocalBalance.MilliSatoshi, staged.LocalBalanceMsat);
        Assert.Equal(channel.RemoteBalance.MilliSatoshi, staged.RemoteBalanceMsat);
        Assert.Empty(staged.Htlcs);
    }

    [Fact]
    public async Task Given_SaveFails_When_FirstChannelReady_Then_NoSnapshotInMemory()
    {
        // Arrange - I2: the snapshot is swapped in only after the save
        var channel = CreateChannel(ChannelState.V1FundingSigned);
        SetupChannel(channel);
        _mockUnitOfWork.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("disk full"));
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, s_secondPoint));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.HandleAsync(message, ChannelState.V1FundingSigned, new FeatureOptions(),
                                       channel.RemoteNodeId));

        // Assert
        Assert.Null(channel.Commitments);
    }

    [Fact]
    public async Task Given_RepeatedChannelReady_When_HandleAsync_Then_NoSecondSnapshotIsCreated()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned);
        SetupChannel(channel);
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, s_secondPoint));
        await _handler.HandleAsync(message, ChannelState.V1FundingSigned, new FeatureOptions(),
                                   channel.RemoteNodeId);
        var first = channel.Commitments;

        // Act
        await _handler.HandleAsync(message, ChannelState.ReadyForThem, new FeatureOptions(), channel.RemoteNodeId);

        // Assert
        Assert.Same(first, channel.Commitments);
        _mockChannelStateDbRepository.Verify(
            r => r.InitializeAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelStateExtras?>()), Times.Once);
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