using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Infrastructure.Protocol.Models;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Node.ValueObjects;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// SP1-A-T3: the per-peer inbound loop groups a <c>start_batch</c> (BOLT 2 "Batching channel messages", splicing
/// plan SP-OP-04 and D15). Each receiver rule of the spec is one test:
/// "If `batch_size` is not strictly greater than 1: MUST ignore the `start_batch` message. SHOULD send a `warning`."
/// "If `batch_size` is strictly greater than 20: MUST send a `warning` and close the connection, or send an `error`
/// and fail the channel." "MUST group the next `batch_size` messages and process them together." "If one of those
/// messages is not for the specified `channel_id`: MUST send a `warning` and close the connection, or send an
/// `error` and fail the channel." "If `message_type` is missing or not set to the type for `commitment_signed`: MUST
/// ignore the `start_batch` message and process the following messages sequentially."
/// </summary>
public class PeerManagerBatchTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private static readonly CompactPubKey s_ourNodeKey =
        new PubKey("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb").ToBytes();

    private static readonly ChannelId s_channelA = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly ChannelId s_channelB = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());

    private readonly CompactPubKey _peerKey =
        new PubKey("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7").ToBytes();

    private readonly Mock<IChannelManager> _mockChannelManager = new();
    private readonly Mock<IPeerServiceFactory> _mockPeerServiceFactory = new();
    private readonly Mock<IPeerService> _mockPeerService = new();
    private readonly Mock<ITcpService> _mockTcpService = new();
    private readonly FakeServiceProvider _fakeServiceProvider = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<IPeerDbRepository> _mockPeerDbRepository = new();
    private readonly Mock<IChannelMemoryRepository> _mockChannelMemoryRepository = new();
    private readonly Mock<ISecureKeyManager> _mockSecureKeyManager = new();

    // What the channel manager was handed, in order: single messages and whole batches
    private readonly List<object> _handled = [];
    private readonly List<WarningException> _warnings = [];
    private readonly TaskCompletionSource<Exception?> _disconnect =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PeerManagerBatchTests()
    {
        _mockPeerService.SetupGet(p => p.PeerPubKey).Returns(_peerKey);
        _mockPeerService.SetupGet(p => p.Features).Returns(new FeatureOptions());
        _mockPeerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>())).Returns(Task.CompletedTask);
        _mockPeerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>()))
                        .Callback((WarningException w) =>
                         {
                             lock (_warnings)
                                 _warnings.Add(w);
                         })
                        .Returns(Task.CompletedTask);
        _mockPeerService.Setup(p => p.Disconnect(It.IsAny<Exception?>()))
                        .Callback((Exception? e) => _disconnect.TrySetResult(e));

        _mockPeerServiceFactory
           .Setup(f => f.CreateConnectedPeerAsync(It.IsAny<CompactPubKey>(), It.IsAny<TcpClient>()))
           .ReturnsAsync(_mockPeerService.Object);

        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback((IChannelMessage message, FeatureOptions _, CompactPubKey _) => Record(message))
           .Returns(Task.CompletedTask);
        _mockChannelManager
           .Setup(cm => cm.HandleCommitmentSignedBatchAsync(It.IsAny<CommitmentSignedBatch>(),
                                                            It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
           .Callback((CommitmentSignedBatch batch, FeatureOptions _, CompactPubKey _) => Record(batch))
           .Returns(Task.CompletedTask);

        _mockUnitOfWork.Setup(u => u.PeerDbRepository).Returns(_mockPeerDbRepository.Object);
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(() => []);
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>())).Returns(Task.CompletedTask);
        _fakeServiceProvider.AddService(typeof(IUnitOfWork), _mockUnitOfWork.Object);

        _mockChannelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        _mockSecureKeyManager.Setup(k => k.GetNodePubKey()).Returns(s_ourNodeKey);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(20)]
    public async Task Given_StartBatchOfCommitmentSigned_When_AllMessagesArrive_Then_TheBatchIsHandedToOneCall(
        int batchSize)
    {
        // Arrange
        await ConnectAsync();
        var messages = Enumerable.Range(0, batchSize).Select(i => CommitmentSigned(s_channelA, (byte)i)).ToArray();
        var sentinel = UpdateFee(s_channelA);

        // Act
        Raise(StartBatch(s_channelA, (ushort)batchSize));
        foreach (var message in messages)
            Raise(message);
        Raise(sentinel);
        await WaitForHandledAsync(2);

        // Assert
        var batch = Assert.IsType<CommitmentSignedBatch>(Handled(0));
        Assert.Equal(s_channelA, batch.ChannelId);
        Assert.Equal(messages, batch.Messages);
        Assert.Same(sentinel, Handled(1));
        _mockChannelManager.Verify(cm => cm.HandleCommitmentSignedBatchAsync(It.IsAny<CommitmentSignedBatch>(),
                                                                             It.IsAny<FeatureOptions>(), _peerKey),
                                   Times.Once);
        Assert.Empty(Warnings());
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Given_BatchSizeAtMostOne_When_Received_Then_IgnoredWithAWarningAndTheNextMessageIsSequential(
        int batchSize)
    {
        // Arrange
        await ConnectAsync();
        var commitmentSigned = CommitmentSigned(s_channelA, 1);

        // Act
        Raise(StartBatch(s_channelA, (ushort)batchSize));
        Raise(commitmentSigned);
        await WaitForHandledAsync(1);

        // Assert
        Assert.Same(commitmentSigned, Handled(0));
        var warning = Assert.IsType<ChannelWarningException>(Assert.Single(Warnings()));
        Assert.Equal(s_channelA, warning.ChannelId);
        Assert.False(warning.CloseConnection);
        VerifyNoBatch();
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(ushort.MaxValue)]
    public async Task Given_BatchSizeAboveTwenty_When_Received_Then_WarningAndClose(int batchSize)
    {
        // Arrange
        await ConnectAsync();

        // Act
        Raise(StartBatch(s_channelA, (ushort)batchSize));
        Raise(CommitmentSigned(s_channelA, 1));
        var reason = await _disconnect.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert (the warning goes out with the disconnect)
        var warning = Assert.IsType<ChannelWarningException>(reason);
        Assert.Equal(s_channelA, warning.ChannelId);
        Assert.True(warning.CloseConnection);
        VerifyNoBatch();
        _mockChannelManager.Verify(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(),
                                                                      It.IsAny<FeatureOptions>(),
                                                                      It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task Given_AMessageForAnotherChannelInTheBatch_When_Received_Then_WarningAndClose()
    {
        // Arrange
        await ConnectAsync();

        // Act
        Raise(StartBatch(s_channelA, 3));
        Raise(CommitmentSigned(s_channelA, 1));
        Raise(CommitmentSigned(s_channelB, 2));
        Raise(CommitmentSigned(s_channelA, 3));
        var reason = await _disconnect.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert (scoped to the batch's channel; nothing of the batch reaches the channel manager)
        var warning = Assert.IsType<ChannelWarningException>(reason);
        Assert.Equal(s_channelA, warning.ChannelId);
        Assert.True(warning.CloseConnection);
        VerifyNoBatch();
        _mockChannelManager.Verify(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(),
                                                                      It.IsAny<FeatureOptions>(),
                                                                      It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task Given_AMessageOtherThanCommitmentSignedInTheBatch_When_Received_Then_WarningAndClose()
    {
        // Arrange (the sender "MUST send `batch_size` `commitment_signed` messages ... without any other unrelated
        // messages in-between")
        await ConnectAsync();

        // Act
        Raise(StartBatch(s_channelA, 2));
        Raise(CommitmentSigned(s_channelA, 1));
        Raise(UpdateFee(s_channelA));
        var reason = await _disconnect.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        var warning = Assert.IsType<ChannelWarningException>(reason);
        Assert.Equal(s_channelA, warning.ChannelId);
        Assert.True(warning.CloseConnection);
        VerifyNoBatch();
    }

    [Fact]
    public async Task Given_StartBatchWithoutMessageType_When_Received_Then_IgnoredAndTheMessagesAreSequential()
    {
        // Arrange
        await ConnectAsync();
        var first = CommitmentSigned(s_channelA, 1);
        var second = CommitmentSigned(s_channelA, 2);

        // Act
        Raise(new StartBatchMessage(new StartBatchPayload(s_channelA, 2)));
        Raise(first);
        Raise(second);
        await WaitForHandledAsync(2);

        // Assert (no warning: the spec only says to ignore it)
        Assert.Same(first, Handled(0));
        Assert.Same(second, Handled(1));
        VerifyNoBatch();
        Assert.Empty(Warnings());
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public async Task Given_StartBatchWithAnotherMessageType_When_Received_Then_IgnoredAndTheMessagesAreSequential()
    {
        // Arrange (message_type 128 = update_add_htlc)
        await ConnectAsync();
        var first = CommitmentSigned(s_channelA, 1);
        var second = CommitmentSigned(s_channelA, 2);

        // Act
        Raise(new StartBatchMessage(new StartBatchPayload(s_channelA, 2),
                                    new StartBatchMessageTypeTlv((ushort)MessageTypes.UpdateAddHtlc)));
        Raise(first);
        Raise(second);
        await WaitForHandledAsync(2);

        // Assert
        Assert.Same(first, Handled(0));
        Assert.Same(second, Handled(1));
        VerifyNoBatch();
        Assert.Empty(Warnings());
    }

    [Fact]
    public async Task Given_TwoBatchesInARow_When_Received_Then_EachIsItsOwnCall()
    {
        // Arrange
        await ConnectAsync();
        var batchOne = new[] { CommitmentSigned(s_channelA, 1), CommitmentSigned(s_channelA, 2) };
        var batchTwo = new[] { CommitmentSigned(s_channelB, 3), CommitmentSigned(s_channelB, 4) };

        // Act
        Raise(StartBatch(s_channelA, 2));
        foreach (var message in batchOne)
            Raise(message);
        Raise(StartBatch(s_channelB, 2));
        foreach (var message in batchTwo)
            Raise(message);
        await WaitForHandledAsync(2);

        // Assert
        var first = Assert.IsType<CommitmentSignedBatch>(Handled(0));
        var second = Assert.IsType<CommitmentSignedBatch>(Handled(1));
        Assert.Equal(s_channelA, first.ChannelId);
        Assert.Equal(batchOne, first.Messages);
        Assert.Equal(s_channelB, second.ChannelId);
        Assert.Equal(batchTwo, second.Messages);
    }

    [Fact]
    public async Task Given_TheBatchHandlerFailsTheChannelWithAWarning_When_Handled_Then_TheWarningIsScopedToTheBatch()
    {
        // Arrange (e.g. SP-OP-05 handled by the channel manager as a warning without a channel id)
        await ConnectAsync();
        _mockChannelManager
           .Setup(cm => cm.HandleCommitmentSignedBatchAsync(It.IsAny<CommitmentSignedBatch>(),
                                                            It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
           .ThrowsAsync(new ChannelWarningException("batch refused", "batch refused"));

        // Act
        Raise(StartBatch(s_channelA, 2));
        Raise(CommitmentSigned(s_channelA, 1));
        Raise(CommitmentSigned(s_channelA, 2));
        await WaitUntilAsync(() => Warnings().Count == 1);

        // Assert
        var warning = Assert.IsType<ChannelWarningException>(Assert.Single(Warnings()));
        Assert.Equal(s_channelA, warning.ChannelId);
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    private void Record(object handled)
    {
        lock (_handled)
            _handled.Add(handled);
    }

    private object Handled(int index)
    {
        lock (_handled)
            return _handled[index];
    }

    private List<WarningException> Warnings()
    {
        lock (_warnings)
            return [.. _warnings];
    }

    private Task WaitForHandledAsync(int count)
    {
        return WaitUntilAsync(() =>
        {
            lock (_handled)
                return _handled.Count >= count;
        });
    }

    private void VerifyNoBatch()
    {
        _mockChannelManager.Verify(cm => cm.HandleCommitmentSignedBatchAsync(It.IsAny<CommitmentSignedBatch>(),
                                                                             It.IsAny<FeatureOptions>(),
                                                                             It.IsAny<CompactPubKey>()),
                                   Times.Never);
    }

    private async Task ConnectAsync()
    {
        var peerManager = new PeerManager(_mockChannelManager.Object, _mockChannelMemoryRepository.Object,
                                          new Mock<ILogger<PeerManager>>().Object, _mockPeerServiceFactory.Object,
                                          _mockSecureKeyManager.Object, _mockTcpService.Object,
                                          _fakeServiceProvider);
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .ReturnsAsync(new ConnectedPeer(_peerKey, "127.0.0.1", 9735, new Mock<TcpClient>().Object));
        await peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_peerKey}@127.0.0.1:9735"));
    }

    private void Raise(IChannelMessage message)
    {
        _mockPeerService.Raise(p => p.OnChannelMessageReceived += null, _mockPeerService.Object,
                               new ChannelMessageEventArgs(message, _peerKey));
    }

    private static StartBatchMessage StartBatch(ChannelId channelId, ushort batchSize)
    {
        return new StartBatchMessage(new StartBatchPayload(channelId, batchSize),
                                     StartBatchMessageTypeTlv.CommitmentSigned());
    }

    private static CommitmentSignedMessage CommitmentSigned(ChannelId channelId, byte marker)
    {
        var signature = new CompactSignature(Enumerable.Repeat(marker, 64).ToArray());
        return new CommitmentSignedMessage(new CommitmentSignedPayload(channelId, [], signature));
    }

    private static UpdateFeeMessage UpdateFee(ChannelId channelId)
    {
        return new UpdateFeeMessage(new UpdateFeePayload(channelId, 5_000));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}