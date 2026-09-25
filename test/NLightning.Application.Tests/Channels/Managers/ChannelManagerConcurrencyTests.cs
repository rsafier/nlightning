using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Managers;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT2 plan N0-T3 (NL-033, NL-193): one channel's transitions never run concurrently, and the messages a transition
/// sends are raised, in order, before the next transition of that channel can start.
/// </summary>
public class ChannelManagerConcurrencyTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly Mock<IBlockchainMonitor> _mockBlockchainMonitor = new();
    private readonly Mock<IChannelMemoryRepository> _mockChannelMemoryRepository = new();
    private readonly Mock<IChannelDbRepository> _mockChannelDbRepository = new();
    private readonly Mock<IMessageFactory> _mockMessageFactory = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<IChannelMessageHandler<ChannelReadyMessage>> _mockChannelReadyHandler = new();
    private readonly List<ChannelModel> _channels = [];

    private int _handlersInside;
    private int _maxHandlersInside;

    public ChannelManagerConcurrencyTests()
    {
        _mockChannelMemoryRepository
           .Setup(r => r.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel>.IsAny!))
           .Returns(new TryGetChannelDelegate((ChannelId id, out ChannelModel channel) =>
            {
                channel = _channels.FirstOrDefault(c => c.ChannelId == id)!;
                return channel is not null;
            }));
        _mockChannelMemoryRepository
           .Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
           .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        _mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_mockChannelDbRepository.Object);
        _mockMessageFactory
           .Setup(f => f.CreateChannelReadyMessage(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                                   It.IsAny<ShortChannelId?>()))
           .Returns((ChannelId id, CompactPubKey point, ShortChannelId? _) =>
                        new ChannelReadyMessage(new ChannelReadyPayload(id, point)));
    }

    private delegate bool TryGetChannelDelegate(ChannelId channelId, out ChannelModel channel);

    [Fact]
    public async Task Given_TwoMessagesSameChannel_When_HandledConcurrently_Then_Serialized()
    {
        // Arrange
        var channelId = CreateChannelId(0x01);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        SetupChannelReadyHandler(async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await gate.Task;
            }

            return [];
        });
        var channelManager = CreateChannelManager();

        // Act
        var first = Task.Run(() => channelManager.HandleChannelMessageAsync(CreateChannelReady(channelId),
                                                                            new FeatureOptions(), s_pubKey),
                             TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        var second = Task.Run(() => channelManager.HandleChannelMessageAsync(CreateChannelReady(channelId),
                                                                             new FeatureOptions(), s_pubKey),
                              TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var callsWhileFirstRuns = Volatile.Read(ref calls);
        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, callsWhileFirstRuns);
        Assert.Equal(2, calls);
        Assert.Equal(1, _maxHandlersInside);
    }

    [Fact]
    public async Task Given_TwoMessagesDifferentChannels_When_HandledConcurrently_Then_BothRunAtOnce()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        SetupChannelReadyHandler(async () =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                bothInside.TrySetResult();
            await gate.Task;
            return [];
        });
        var channelManager = CreateChannelManager();

        // Act
        var first = channelManager.HandleChannelMessageAsync(CreateChannelReady(CreateChannelId(0x02)),
                                                             new FeatureOptions(), s_pubKey);
        var second = channelManager.HandleChannelMessageAsync(CreateChannelReady(CreateChannelId(0x03)),
                                                              new FeatureOptions(), s_pubKey);
        await bothInside.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, _maxHandlersInside);
    }

    [Fact]
    public async Task Given_HandlerReturnsThree_When_Handled_Then_RaisedInOrderBeforeCompletion()
    {
        // Arrange
        var channelId = CreateChannelId(0x04);
        IReadOnlyList<IChannelMessage> replies =
        [
            CreateChannelReady(channelId), CreateChannelReady(channelId), CreateChannelReady(channelId)
        ];
        SetupChannelReadyHandler(() => Task.FromResult(replies));
        var channelManager = CreateChannelManager();
        var raised = new List<IChannelMessage>();
        channelManager.OnResponseMessageReady += (_, args) => raised.Add(args.ResponseMessage);

        // Act
        await channelManager.HandleChannelMessageAsync(CreateChannelReady(channelId), new FeatureOptions(), s_pubKey);

        // Assert
        Assert.Equal(replies, raised);
    }

    [Fact]
    public async Task Given_FundingConfirmedWhileMessageRuns_When_SameChannel_Then_ChannelReadyFollowsTheReply()
    {
        // Arrange (before N0-T3 the confirmation ran at once, mutating the channel under the running handler and
        // sending channel_ready ahead of that handler's reply)
        var channel = CreateChannel(ChannelState.ReadyForThem, 0x05);
        _channels.Add(channel);
        var reply = CreateChannelReady(channel.ChannelId);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupChannelReadyHandler(async () =>
        {
            entered.TrySetResult();
            await gate.Task;
            return [reply];
        });
        var channelManager = CreateChannelManager();
        var raised = new List<IChannelMessage>();
        var twoRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channelManager.OnResponseMessageReady += (_, args) =>
        {
            lock (raised)
            {
                raised.Add(args.ResponseMessage);
                if (raised.Count == 2)
                    twoRaised.TrySetResult();
            }
        };

        // Act
        var handling = Task.Run(() => channelManager.HandleChannelMessageAsync(CreateChannelReady(channel.ChannelId),
                                                                               new FeatureOptions(), s_pubKey),
                                TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        RaiseFundingConfirmed(channel);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var stateWhileHandlerRuns = channel.State;
        int raisedWhileHandlerRuns;
        lock (raised)
            raisedWhileHandlerRuns = raised.Count;
        gate.SetResult();
        await handling.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await twoRaised.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.ReadyForThem, stateWhileHandlerRuns);
        Assert.Equal(0, raisedWhileHandlerRuns);
        Assert.Same(reply, raised[0]);
        Assert.NotSame(reply, raised[1]);
        Assert.IsType<ChannelReadyMessage>(raised[1]);
        Assert.Equal(ChannelState.Open, channel.State);
    }

    [Fact]
    public async Task Given_TransitionHoldsTheTemporaryChannelLock_When_OpeningStarts_Then_OpenChannelFollowsItsMessages()
    {
        // Arrange (open_channel used to be sent straight through IPeerService, outside the lock and the outbox)
        var channel = CreateChannel(ChannelState.V1Opening, 0x06);
        var earlierMessage = CreateChannelReady(channel.ChannelId);
        var openChannel = CreateChannelReady(channel.ChannelId);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupChannelReadyHandler(async () =>
        {
            entered.TrySetResult();
            await gate.Task;
            return [earlierMessage];
        });
        var channelManager = CreateChannelManager();
        var events = new List<object>();
        channelManager.OnResponseMessageReady += (_, args) =>
        {
            lock (events)
                events.Add(args.ResponseMessage);
        };
        _mockChannelMemoryRepository.Setup(r => r.AddTemporaryChannel(s_pubKey, channel))
                                    .Callback(() =>
                                     {
                                         lock (events)
                                             events.Add("stored");
                                     });
        var handling = Task.Run(() => channelManager.HandleChannelMessageAsync(CreateChannelReady(channel.ChannelId),
                                                                               new FeatureOptions(), s_pubKey),
                                TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Act
        var opening = channelManager.StartOpeningChannelAsync(s_pubKey, channel, openChannel);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        int eventsWhileLocked;
        lock (events)
            eventsWhileLocked = events.Count;
        gate.SetResult();
        await Task.WhenAll(handling, opening).WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, eventsWhileLocked);
        Assert.Equal(new object[] { earlierMessage, "stored", openChannel }, events);
    }

    private void SetupChannelReadyHandler(Func<Task<IReadOnlyList<IChannelMessage>>> body)
    {
        _mockChannelReadyHandler
           .Setup(h => h.HandleAsync(It.IsAny<ChannelReadyMessage>(), It.IsAny<ChannelState>(),
                                     It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
           .Returns(async () =>
            {
                var now = Interlocked.Increment(ref _handlersInside);
                InterlockedMax(ref _maxHandlersInside, now);
                try
                {
                    return await body();
                }
                finally
                {
                    Interlocked.Decrement(ref _handlersInside);
                }
            });
    }

    private void RaiseFundingConfirmed(ChannelModel channel)
    {
        var watchedTransaction = new WatchedTransactionModel(channel.ChannelId,
                                                             channel.FundingOutput!.TransactionId!.Value, 3);
        watchedTransaction.SetHeightAndIndex(100, 0);
        watchedTransaction.MarkAsCompleted();
        _mockBlockchainMonitor.Raise(m => m.OnTransactionConfirmed += null,
                                     new TransactionConfirmedEventArgs(watchedTransaction, 103));
    }

    private ChannelManager CreateChannelManager()
    {
        var mockSigner = new Mock<ILightningSigner>();
        var serviceProvider = new FakeServiceProvider();
        serviceProvider.AddService(typeof(IChannelMessageHandler<ChannelReadyMessage>),
                                   _mockChannelReadyHandler.Object);
        serviceProvider.AddService(typeof(IUnitOfWork), _mockUnitOfWork.Object);
        serviceProvider.AddService(typeof(ChannelDomainEventQueue), new ChannelDomainEventQueue());
        serviceProvider.AddService(typeof(FundingConfirmedMessageHandler),
                                   new FundingConfirmedMessageHandler(_mockChannelMemoryRepository.Object,
                                                                      mockSigner.Object,
                                                                      new Mock<ILogger<FundingConfirmedMessageHandler>>()
                                                                         .Object,
                                                                      _mockMessageFactory.Object,
                                                                      _mockUnitOfWork.Object));

        return new ChannelManager(_mockBlockchainMonitor.Object, new ChannelLockProvider(),
                                  _mockChannelMemoryRepository.Object, new Mock<ILogger<ChannelManager>>().Object,
                                  mockSigner.Object, serviceProvider);
    }

    private static ChannelReadyMessage CreateChannelReady(ChannelId channelId)
    {
        return new ChannelReadyMessage(new ChannelReadyPayload(channelId, s_pubKey));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
                return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static ChannelId CreateChannelId(byte fill)
    {
        return new ChannelId(Enumerable.Repeat(fill, 32).ToArray());
    }

    private static ChannelModel CreateChannel(ChannelState state, byte id)
    {
        var channelIdBytes = new byte[32];
        channelIdBytes[0] = id;
        var txIdBytes = new byte[32];
        txIdBytes[0] = id;

        var fundingAmount = LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(fundingAmount, s_pubKey, s_pubKey)
        {
            TransactionId = new TxId(txIdBytes),
            Index = 0
        };
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        var commitmentNumber = new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256());

        return new ChannelModel(channelConfig, new ChannelId(channelIdBytes), commitmentNumber, fundingOutput, false,
                                null, null, LightningMoney.Zero, keySet, 0, 0, fundingAmount, keySet, 0, s_pubKey, 0,
                                state, ChannelVersion.V1)
        {
            FundingCreatedAtBlockHeight = 100
        };
    }
}