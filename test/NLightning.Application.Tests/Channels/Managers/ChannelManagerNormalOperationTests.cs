using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Managers;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// <c>ChannelManager</c> wiring of the BOLT 2 normal-operation messages (N6-T1): dispatch to the handlers, the HTLC
/// switch outside the lock, the failed-channel path (N6-T3 part), the funding_created double lock (NL-235) and the
/// startup handling of reloaded snapshots.
/// </summary>
public class ChannelManagerNormalOperationTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly NormalOperationTestContext _context = new();
    private readonly ChannelLockProvider _lockProvider = new();
    private readonly Mock<IHtlcSwitch> _htlcSwitch = new();
    private readonly Mock<IChannelIdFactory> _channelIdFactory = new();
    private readonly ServiceCollection _services = [];

    public ChannelManagerNormalOperationTests()
    {
        _context.ChannelMemoryRepository.Setup(r => r.TryGetChannelState(TestChannelId, out It.Ref<ChannelState>.IsAny))
                .Returns(new TryGetStateDelegate((ChannelId _, out ChannelState state) =>
                 {
                     state = _context.Channel.State;
                     return true;
                 }));
        _services.AddScoped(_ => _context.UnitOfWork.Object);
        _services.AddScoped<ChannelDomainEventQueue>();
        _services.AddSingleton<IMessageFactory>(_context.MessageFactory);
        _services.AddSingleton(_context.MessageSerializer.Object);
        _services.AddSingleton(_channelIdFactory.Object);
    }

    private delegate bool TryGetStateDelegate(ChannelId channelId, out ChannelState state);

    [Theory]
    [InlineData(MessageTypes.UpdateAddHtlc)]
    [InlineData(MessageTypes.UpdateFulfillHtlc)]
    [InlineData(MessageTypes.UpdateFailHtlc)]
    [InlineData(MessageTypes.UpdateFailMalformedHtlc)]
    [InlineData(MessageTypes.CommitmentSigned)]
    [InlineData(MessageTypes.RevokeAndAck)]
    [InlineData(MessageTypes.UpdateFee)]
    public async Task Given_NormalOperationMessage_When_Handled_Then_ItsHandlerRunsAndTheRepliesAreRaised(
        MessageTypes messageType)
    {
        // Arrange
        var message = CreateMessage(messageType);
        var reply = _context.MessageFactory.CreateUpdateFeeMessage(TestChannelId, 1);
        RegisterHandlerReturning(message, [reply]);
        var channelManager = CreateChannelManager();
        var raised = new List<IChannelMessage>();
        channelManager.OnResponseMessageReady += (_, args) => raised.Add(args.ResponseMessage);

        // Act
        await channelManager.HandleChannelMessageAsync(message, new FeatureOptions(), PeerNodeId);

        // Assert - raised once, only through the event (NL-234: nothing is returned to send twice)
        Assert.Same(reply, Assert.Single(raised));
    }

    [Fact]
    public async Task Given_TransitionRaisedEvents_When_Handled_Then_TheSwitchGetsThemAfterTheLockIsReleased()
    {
        // Arrange - the switch may call IChannelOperations, which take the channel's lock
        _services.AddScoped<IChannelMessageHandler<UpdateAddHtlcMessage>>(sp => new EventRaisingHandler(
                                                                              sp.GetRequiredService<
                                                                                  ChannelDomainEventQueue>()));
        var lockWasFree = false;
        _htlcSwitch.Setup(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()))
                   .Returns(async () =>
                    {
                        using var cts = new CancellationTokenSource(s_timeout);
                        using var channelLock = await _lockProvider.AcquireAsync(TestChannelId, cts.Token);
                        lockWasFree = true;
                    });
        _services.AddSingleton(_htlcSwitch.Object);
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.HandleChannelMessageAsync(CreateMessage(MessageTypes.UpdateAddHtlc),
                                                       new FeatureOptions(), PeerNodeId);

        // Assert
        Assert.True(lockWasFree);
        _htlcSwitch.Verify(s => s.HandleAsync(It.IsAny<IncomingHtlcLockedIn>(), It.IsAny<CancellationToken>()),
                           Times.Once);
    }

    [Fact]
    public async Task Given_SwitchFails_When_Handled_Then_TheMessageStillSucceeds()
    {
        // Arrange - the events are re-derivable; the peer must not be disconnected for our follow-up work
        _services.AddScoped<IChannelMessageHandler<UpdateAddHtlcMessage>>(sp => new EventRaisingHandler(
                                                                              sp.GetRequiredService<
                                                                                  ChannelDomainEventQueue>()));
        _htlcSwitch.Setup(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("switch bug"));
        _services.AddSingleton(_htlcSwitch.Object);
        var channelManager = CreateChannelManager();

        // Act
        var raised = new List<IChannelMessage>();
        channelManager.OnResponseMessageReady += (_, args) => raised.Add(args.ResponseMessage);
        await channelManager.HandleChannelMessageAsync(CreateMessage(MessageTypes.UpdateAddHtlc),
                                                       new FeatureOptions(), PeerNodeId);

        // Assert
        Assert.Empty(raised);
    }

    [Fact]
    public async Task Given_HandlerFailsTheChannel_When_Handled_Then_FailedStateAndErrorArePersistedBeforeRethrow()
    {
        // Arrange - N6-T3: persist Failed + the error before it is sent (B2-RE-05 re-sends it)
        var message = CreateMessage(MessageTypes.RevokeAndAck);
        var failure = new ChannelFailedException(TestChannelId, "[B2-RAA-R01] bad secret", "bad per_commitment_secret");
        var handler = new Mock<IChannelMessageHandler<RevokeAndAckMessage>>();
        handler.Setup(h => h.HandleAsync(It.IsAny<RevokeAndAckMessage>(), It.IsAny<ChannelState>(),
                                         It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
               .ThrowsAsync(failure);
        _services.AddSingleton(handler.Object);
        _context.ChannelDbRepository.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                .Callback(() => _context.Calls.Add("update"))
                .Returns(Task.CompletedTask);
        var channelManager = CreateChannelManager();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => channelManager.HandleChannelMessageAsync(message, new FeatureOptions(),
                                                                           PeerNodeId));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(ChannelState.Failed, _context.Channel.State);
        Assert.NotNull(_context.Channel.ErrorSent);
        Assert.Equal((byte)((ushort)MessageTypes.Error >> 8), _context.Channel.ErrorSent!.Value.Span[0]);
        Assert.Equal((byte)MessageTypes.Error, _context.Channel.ErrorSent!.Value.Span[1]);
        Assert.Equal(["update", "save"], _context.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_HandlerFailsTheChannel_When_BroadcastIsRequired_Then_TheFailureServiceGetsItAfterTheLock(
        bool mustBroadcast)
    {
        // Arrange - B2-RE-14 (reestablish with next_commitment_number 0): broadcast our commitment, never under the lock
        var message = CreateMessage(MessageTypes.RevokeAndAck);
        var failure = new ChannelFailedException(TestChannelId, "[B2-RE-14] peer lost its state", "state lost")
        {
            MustBroadcast = mustBroadcast
        };
        var handler = new Mock<IChannelMessageHandler<RevokeAndAckMessage>>();
        handler.Setup(h => h.HandleAsync(It.IsAny<RevokeAndAckMessage>(), It.IsAny<ChannelState>(),
                                         It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
               .ThrowsAsync(failure);
        _services.AddSingleton(handler.Object);
        _context.ChannelDbRepository.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>())).Returns(Task.CompletedTask);
        var lockFreeDuringBroadcast = false;
        var failureService = new Mock<IChannelFailureService>();
        failureService.Setup(f => f.FailChannelAsync(failure, It.IsAny<CancellationToken>()))
                      .Returns(async () =>
                       {
                           using var probe = await _lockProvider.AcquireAsync(TestChannelId)
                                                                .WaitAsync(TimeSpan.FromSeconds(5));
                           lockFreeDuringBroadcast = true;
                           return new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, null);
                       });
        _services.AddSingleton(failureService.Object);
        var channelManager = CreateChannelManager();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => channelManager.HandleChannelMessageAsync(message, new FeatureOptions(),
                                                                           PeerNodeId));

        // Assert - the error still leaves for the peer; the broadcast is asked for only when required
        Assert.Same(failure, exception);
        Assert.Equal(ChannelState.Failed, _context.Channel.State);
        failureService.Verify(f => f.FailChannelAsync(failure, It.IsAny<CancellationToken>()),
                              mustBroadcast ? Times.Once() : Times.Never());
        Assert.Equal(mustBroadcast, lockFreeDuringBroadcast);
    }

    [Fact]
    public async Task Given_FailedChannel_When_UpdateArrives_Then_TheChannelErrorIsRaisedAgain()
    {
        // Arrange - a failed channel ignores every message and re-sends its error (B2-RE-05), without the handler
        var context = new NormalOperationTestContext(state: ChannelState.Failed);
        context.ChannelMemoryRepository.Setup(r => r.TryGetChannelState(TestChannelId, out It.Ref<ChannelState>.IsAny))
               .Returns(new TryGetStateDelegate((ChannelId _, out ChannelState state) =>
                {
                    state = ChannelState.Failed;
                    return true;
                }));
        var services = new ServiceCollection();
        services.AddSingleton<IChannelMessageHandler<UpdateAddHtlcMessage>>(
            new UpdateAddHtlcMessageHandler(NullLogger<UpdateAddHtlcMessageHandler>.Instance,
                                            context.CreateTransitions()));
        services.AddScoped<ChannelDomainEventQueue>();
        var channelManager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, _lockProvider,
                                                context.ChannelMemoryRepository.Object,
                                                NullLogger<ChannelManager>.Instance, context.LightningSigner.Object,
                                                services.BuildServiceProvider());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => channelManager.HandleChannelMessageAsync(
                                UpdateAddHtlcMessageHandlerTests.CreateAdd(0, 50_000_000), new FeatureOptions(),
                                PeerNodeId));

        // Assert - the error names the channel; nothing is persisted again
        Assert.Equal(TestChannelId, exception.ChannelId);
        Assert.Equal(ChannelFailedException.DefaultPeerMessage, exception.PeerMessage);
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task Given_FundingCreated_When_Handled_Then_TheRealChannelIdIsLockedToo()
    {
        // Arrange - NL-235: the fundee's handler creates the channel under its real id
        var temporaryChannelId = new ChannelId(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var realChannelId = new ChannelId(Enumerable.Repeat((byte)0x22, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x33, 32).ToArray());
        _channelIdFactory.Setup(f => f.CreateV1(fundingTxId, 1)).Returns(realChannelId);
        var realLockWasHeld = false;
        var handler = new Mock<IChannelMessageHandler<FundingCreatedMessage>>();
        handler.Setup(h => h.HandleAsync(It.IsAny<FundingCreatedMessage>(), It.IsAny<ChannelState>(),
                                         It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
               .Returns(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                    try
                    {
                        using var realLock = await _lockProvider.AcquireAsync(realChannelId, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        realLockWasHeld = true;
                    }

                    return [];
                });
        _services.AddSingleton(handler.Object);
        var channelManager = CreateChannelManager();
        var message = new FundingCreatedMessage(new FundingCreatedPayload(temporaryChannelId, fundingTxId, 1,
                                                                          Signature(1)));

        // Act
        await channelManager.HandleChannelMessageAsync(message, new FeatureOptions(), PeerNodeId);

        // Assert - held during the handler, released afterwards
        Assert.True(realLockWasHeld);
        using var cts = new CancellationTokenSource(s_timeout);
        using var afterwards = await _lockProvider.AcquireAsync(realChannelId, cts.Token);
        using var temporaryAfterwards = await _lockProvider.AcquireAsync(temporaryChannelId, cts.Token);
    }

    [Fact]
    public async Task Given_ReloadedChannelWithUnsignedPeerAdd_When_Registered_Then_TheRevertIsPersisted()
    {
        // Arrange - BOLT 2 retransmission: the peer's unsigned updates are dropped before any reestablish
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.RegisterExistingChannelAsync(_context.Channel);

        // Assert
        Assert.Equal(["apply", "save"], _context.Calls);
        Assert.Single(_context.Applied[0].Transition.DroppedHtlcs);
        Assert.Empty(_context.State.Htlcs);
        Assert.Equal(0UL, _context.State.RemoteNextHtlcId);
        _context.LightningSigner.Verify(s => s.RegisterChannel(TestChannelId, It.IsAny<ChannelSigningInfo>()),
                                        Times.Once);
    }

    [Fact]
    public async Task Given_ReloadedChannelWithNothingUnsigned_When_Registered_Then_NothingIsPersisted()
    {
        // Arrange
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.RegisterExistingChannelAsync(_context.Channel);

        // Assert
        Assert.Empty(_context.Calls);
        _context.ChannelMemoryRepository.Verify(r => r.AddChannel(_context.Channel), Times.Once);
    }

    [Fact]
    public async Task Given_ReloadedChannelWithPendingEvents_When_Registered_Then_TheyAreReplayedToTheSwitchOutsideTheLock()
    {
        // Arrange - I8: a crash after the revoke_and_ack that locked an HTLC in (event not handled yet), and a settled
        // HTLC whose archive was not pruned yet
        var lockedIn = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(1));
        var settled = new HtlcRecord(HtlcDirection.Outgoing, 7, 10_000_000, HashOf(SecretOf(2)), 600,
                                     HtlcState.RcvdRemoveAckRevocation, HtlcRemoval.Fail(new byte[292]));
        _context.ChannelStateDbRepository
                .Setup(r => r.LoadAsync(TestChannelId, It.IsAny<CommitmentParams>()))
                .ReturnsAsync(new PersistedChannelState(_context.State, [settled], null,
                                                        LastSentCommitmentMessage.None, []));
        var replayed = new List<IChannelDomainEvent>();
        var lockWasFree = true;
        _htlcSwitch.Setup(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()))
                   .Returns(async (IChannelDomainEvent channelEvent, CancellationToken _) =>
                    {
                        replayed.Add(channelEvent);
                        using var cts = new CancellationTokenSource(s_timeout);
                        try
                        {
                            using var channelLock = await _lockProvider.AcquireAsync(TestChannelId, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            lockWasFree = false;
                        }
                    });
        _services.AddSingleton(_htlcSwitch.Object);
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.RegisterExistingChannelAsync(_context.Channel);

        // Assert
        Assert.True(lockWasFree);
        Assert.Collection(replayed,
                          e => Assert.Equal(lockedIn.Id, Assert.IsType<IncomingHtlcLockedIn>(e).HtlcId),
                          e => Assert.Equal(7UL, Assert.IsType<OutgoingHtlcFailed>(e).HtlcId),
                          e => Assert.Equal(7UL, Assert.IsType<OutgoingHtlcSettled>(e).HtlcId));
    }

    [Fact]
    public async Task Given_TheStateCannotBeRead_When_Registered_Then_TheChannelIsStillRegistered()
    {
        // Arrange - one unreadable channel must not stop the startup
        _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(1));
        _context.ChannelStateDbRepository
                .Setup(r => r.LoadAsync(TestChannelId, It.IsAny<CommitmentParams>()))
                .ThrowsAsync(new InvalidOperationException("legacy HTLC rows"));
        _services.AddSingleton(_htlcSwitch.Object);
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.RegisterExistingChannelAsync(_context.Channel);

        // Assert
        _context.ChannelMemoryRepository.Verify(r => r.AddChannel(_context.Channel), Times.Once);
        _htlcSwitch.Verify(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    [Fact]
    public async Task Given_ChannelReadyOpensTheChannel_When_Handled_Then_ItsLinkIsPinnedToTheConnection()
    {
        // Arrange - the channel turns Open on this connection: its updates may go out on it
        var state = ChannelState.ReadyForUs;
        _context.ChannelMemoryRepository.Setup(r => r.TryGetChannelState(TestChannelId, out It.Ref<ChannelState>.IsAny))
                .Returns(new TryGetStateDelegate((ChannelId _, out ChannelState current) =>
                 {
                     current = state;
                     return true;
                 }));
        var handler = new Mock<IChannelMessageHandler<ChannelReadyMessage>>();
        handler.Setup(h => h.HandleAsync(It.IsAny<ChannelReadyMessage>(), It.IsAny<ChannelState>(),
                                         It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
               .ReturnsAsync(() =>
                {
                    state = ChannelState.Open;
                    return [];
                });
        _services.AddSingleton(handler.Object);
        var probe = new Mock<IPeerLivenessProbe>();
        _services.AddSingleton(probe.Object);
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.HandleChannelMessageAsync(new ChannelReadyMessage(new ChannelReadyPayload(TestChannelId,
                                                               Point(0x30))),
                                                       new FeatureOptions(), PeerNodeId);

        // Assert
        probe.Verify(p => p.MarkLinkUp(TestChannelId, PeerNodeId), Times.Once);
    }

    [Fact]
    public async Task Given_AlreadyOpenChannel_When_AMessageArrives_Then_ItsLinkIsNotPinnedAgain()
    {
        // Arrange - a message on a new connection (no channel_reestablish yet, N7) must not make it usable
        RegisterHandlerReturning(CreateMessage(MessageTypes.UpdateFee), []);
        var probe = new Mock<IPeerLivenessProbe>();
        _services.AddSingleton(probe.Object);
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.HandleChannelMessageAsync(CreateMessage(MessageTypes.UpdateFee), new FeatureOptions(),
                                                       PeerNodeId);

        // Assert
        probe.Verify(p => p.MarkLinkUp(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task Given_ReloadedChannel_When_Registered_Then_ItsLinkStaysDownUntilReestablish()
    {
        // Arrange - the startup replay must not send a removal before the peer connects and reestablishes
        _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(1));
        var probe = new Mock<IPeerLivenessProbe>();
        _services.AddSingleton(probe.Object);
        _services.AddSingleton(_htlcSwitch.Object);
        var channelManager = CreateChannelManager();

        // Act
        await channelManager.RegisterExistingChannelAsync(_context.Channel);

        // Assert
        probe.Verify(p => p.MarkLinkUp(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public void Given_Messages_When_Published_Then_TheyAreRaisedInOrder()
    {
        // Arrange - the send side (IChannelOperations, commit scheduler) publishes through the channel manager
        var channelManager = CreateChannelManager();
        var raised = new List<(CompactPubKey Peer, IChannelMessage Message)>();
        channelManager.OnResponseMessageReady += (_, args) => raised.Add((args.PeerPubKey, args.ResponseMessage));
        var first = _context.MessageFactory.CreateUpdateFeeMessage(TestChannelId, 1);
        var second = _context.MessageFactory.CreateUpdateFeeMessage(TestChannelId, 2);

        // Act
        channelManager.Publish(PeerNodeId, [first, second]);

        // Assert
        Assert.Equal([(PeerNodeId, (IChannelMessage)first), (PeerNodeId, second)], raised);
    }

    [Fact]
    public async Task Given_FailedChannel_When_Registered_Then_ItIsLoadedIntoMemory()
    {
        // Arrange - a failed channel stays known, so its updates are refused and its error can be re-sent
        var context = new NormalOperationTestContext(state: ChannelState.Failed);
        var channelManager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, _lockProvider,
                                                context.ChannelMemoryRepository.Object,
                                                NullLogger<ChannelManager>.Instance, context.LightningSigner.Object,
                                                new ServiceCollection().BuildServiceProvider());

        // Act
        await channelManager.RegisterExistingChannelAsync(context.Channel);

        // Assert
        context.ChannelMemoryRepository.Verify(r => r.AddChannel(context.Channel), Times.Once);
        context.LightningSigner.Verify(s => s.RegisterChannel(TestChannelId, It.IsAny<ChannelSigningInfo>()),
                                       Times.Once);
    }

    private ChannelManager CreateChannelManager() =>
        new(new Mock<IBlockchainMonitor>().Object, _lockProvider, _context.ChannelMemoryRepository.Object,
            NullLogger<ChannelManager>.Instance, _context.LightningSigner.Object, _services.BuildServiceProvider());

    private void RegisterHandlerReturning(IChannelMessage message, IReadOnlyList<IChannelMessage> replies)
    {
        switch (message)
        {
            case UpdateAddHtlcMessage:
                _services.AddSingleton(HandlerReturning<UpdateAddHtlcMessage>(replies));
                break;
            case UpdateFulfillHtlcMessage:
                _services.AddSingleton(HandlerReturning<UpdateFulfillHtlcMessage>(replies));
                break;
            case UpdateFailHtlcMessage:
                _services.AddSingleton(HandlerReturning<UpdateFailHtlcMessage>(replies));
                break;
            case UpdateFailMalformedHtlcMessage:
                _services.AddSingleton(HandlerReturning<UpdateFailMalformedHtlcMessage>(replies));
                break;
            case CommitmentSignedMessage:
                _services.AddSingleton(HandlerReturning<CommitmentSignedMessage>(replies));
                break;
            case RevokeAndAckMessage:
                _services.AddSingleton(HandlerReturning<RevokeAndAckMessage>(replies));
                break;
            case UpdateFeeMessage:
                _services.AddSingleton(HandlerReturning<UpdateFeeMessage>(replies));
                break;
        }
    }

    private static IChannelMessageHandler<T> HandlerReturning<T>(IReadOnlyList<IChannelMessage> replies)
        where T : IChannelMessage
    {
        var handler = new Mock<IChannelMessageHandler<T>>();
        handler.Setup(h => h.HandleAsync(It.IsAny<T>(), It.IsAny<ChannelState>(), It.IsAny<FeatureOptions>(),
                                         It.IsAny<CompactPubKey>()))
               .ReturnsAsync(replies);
        return handler.Object;
    }

    private static IChannelMessage CreateMessage(MessageTypes messageType) => messageType switch
    {
        MessageTypes.UpdateAddHtlc => UpdateAddHtlcMessageHandlerTests.CreateAdd(0, 50_000_000),
        MessageTypes.UpdateFulfillHtlc =>
            new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, 0, SecretOf(1))),
        MessageTypes.UpdateFailHtlc => new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(TestChannelId, 0, new byte[8])),
        MessageTypes.UpdateFailMalformedHtlc =>
            new UpdateFailMalformedHtlcMessage(new UpdateFailMalformedHtlcPayload(TestChannelId, 0xC005, 0, new byte[32])),
        MessageTypes.CommitmentSigned =>
            new CommitmentSignedMessage(new CommitmentSignedPayload(TestChannelId, [], Signature(1))),
        MessageTypes.RevokeAndAck =>
            new RevokeAndAckMessage(new RevokeAndAckPayload(TestChannelId, Point(0x22), (byte[])SecretOf(1))),
        MessageTypes.UpdateFee => new UpdateFeeMessage(new UpdateFeePayload(TestChannelId, 5_000)),
        _ => throw new ArgumentOutOfRangeException(nameof(messageType))
    };

    /// <summary>A handler that queues a lock-in event, as a persisted transition would.</summary>
    private sealed class EventRaisingHandler(ChannelDomainEventQueue queue)
        : IChannelMessageHandler<UpdateAddHtlcMessage>
    {
        public Task<IReadOnlyList<IChannelMessage>> HandleAsync(UpdateAddHtlcMessage message,
                                                                ChannelState currentState,
                                                                FeatureOptions negotiatedFeatures,
                                                                CompactPubKey peerPubKey)
        {
            var htlc = new HtlcRecord(HtlcDirection.Incoming, 0, 1_000_000, HashOf(SecretOf(1)), 600,
                                      HtlcState.RcvdAddAckRevocation);
            queue.Enqueue([new IncomingHtlcLockedIn(TestChannelId, htlc)]);
            return Task.FromResult<IReadOnlyList<IChannelMessage>>([]);
        }
    }
}