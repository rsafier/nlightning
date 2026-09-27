using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Managers;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Splicing plan Q1-T1 (NL-019): BOLT 2 <c>stfu</c> is a channel message. <c>ChannelManager</c> routes it to
/// <see cref="StfuMessageHandler"/> under the channel's lock, and the handler's reply goes out through
/// <c>OnResponseMessageReady</c> before the lock is released.
/// </summary>
public class ChannelManagerStfuRoutingTests
{
    private static readonly CompactPubKey s_peerPubKey = CreateKey(0x02);
    private static readonly CompactPubKey s_otherPubKey = CreateKey(0x03);

    private readonly Mock<IChannelMemoryRepository> _mockChannelMemoryRepository = new();
    private readonly Mock<IChannelDbRepository> _mockChannelDbRepository = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<IQuiescenceService> _mockQuiescenceService = new();
    private readonly TrackingLockProvider _lockProvider = new();
    private readonly ChannelModel _channel;

    public ChannelManagerStfuRoutingTests()
    {
        _channel = CreateOpenChannel(0x07);
        _mockChannelMemoryRepository
           .Setup(r => r.TryGetChannel(_channel.ChannelId, out It.Ref<ChannelModel>.IsAny!))
           .Returns(new TryGetChannelDelegate((ChannelId _, out ChannelModel channel) =>
            {
                channel = _channel;
                return true;
            }));
        _mockChannelMemoryRepository
           .Setup(r => r.TryGetChannelState(_channel.ChannelId, out It.Ref<ChannelState>.IsAny))
           .Returns(new TryGetChannelStateDelegate((ChannelId _, out ChannelState state) =>
            {
                state = ChannelState.Open;
                return true;
            }));
        _mockChannelMemoryRepository
           .Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
           .Returns((Func<ChannelModel, bool> predicate) => new[] { _channel }.Where(predicate).ToList());
        _mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_mockChannelDbRepository.Object);
    }

    private delegate bool TryGetChannelDelegate(ChannelId channelId, out ChannelModel channel);

    private delegate bool TryGetChannelStateDelegate(ChannelId channelId, out ChannelState channelState);

    [Fact]
    public async Task Given_StfuFromTheChannelPeer_When_Handled_Then_ReachesTheQuiescenceServiceUnderTheChannelLock()
    {
        // Arrange: BOLT 2 "Channel Quiescence": "The receiver of stfu: ... otherwise: ... MUST reply with stfu once it
        // can do so". The service returns the reply stfu(initiator = 0).
        var reply = new StfuMessage(new StfuPayload(_channel.ChannelId, false));
        var heldDuringCall = false;
        _mockQuiescenceService
           .Setup(s => s.OnStfuReceived(It.IsAny<ChannelModel>(), It.IsAny<StfuPayload>(), It.IsAny<FeatureSet>()))
           .Callback(() => heldDuringCall = _lockProvider.IsHeld(_channel.ChannelId))
           .Returns(reply);
        var raised = new List<(IChannelMessage Message, bool LockHeld)>();
        var channelManager = CreateChannelManager(withQuiescenceService: true);
        channelManager.OnResponseMessageReady += (_, args) =>
            raised.Add((args.ResponseMessage, _lockProvider.IsHeld(_channel.ChannelId)));
        var stfu = new StfuMessage(new StfuPayload(_channel.ChannelId, true));

        // Act
        await channelManager.HandleChannelMessageAsync(stfu, CreateNegotiatedFeatures(), s_peerPubKey);

        // Assert
        Assert.True(heldDuringCall);
        _mockQuiescenceService.Verify(
            s => s.OnStfuReceived(_channel, stfu.Payload,
                                  It.Is<FeatureSet>(f => f.HasFeature(Feature.OptionQuiesce))), Times.Once);
        var (message, lockHeld) = Assert.Single(raised);
        Assert.Same(reply, message);
        Assert.True(lockHeld);
        Assert.Equal(0, _lockProvider.HeldCount);
    }

    [Fact]
    public async Task Given_TheServiceOwesNoReplyYet_When_StfuHandled_Then_NothingIsSent()
    {
        // Arrange: the reply waits until our pending changes are committed and revoked (Q-R-02, TryReleaseStfu)
        _mockQuiescenceService
           .Setup(s => s.OnStfuReceived(It.IsAny<ChannelModel>(), It.IsAny<StfuPayload>(), It.IsAny<FeatureSet>()))
           .Returns((StfuMessage?)null);
        var raised = new List<IChannelMessage>();
        var channelManager = CreateChannelManager(withQuiescenceService: true);
        channelManager.OnResponseMessageReady += (_, args) => raised.Add(args.ResponseMessage);

        // Act
        await channelManager.HandleChannelMessageAsync(new StfuMessage(new StfuPayload(_channel.ChannelId, true)),
                                                       CreateNegotiatedFeatures(), s_peerPubKey);

        // Assert
        Assert.Empty(raised);
        _mockQuiescenceService.Verify(
            s => s.OnStfuReceived(_channel, It.IsAny<StfuPayload>(), It.IsAny<FeatureSet>()), Times.Once);
    }

    [Fact]
    public async Task Given_TheServiceRejectsASecondStfu_When_Handled_Then_WarningClosesTheConnection()
    {
        // Arrange: "MUST NOT send stfu twice" (Q-S-03): the service answers with a warning that closes the connection
        _mockQuiescenceService
           .Setup(s => s.OnStfuReceived(It.IsAny<ChannelModel>(), It.IsAny<StfuPayload>(), It.IsAny<FeatureSet>()))
           .Throws(QuiescenceRules.CreateWarning(QuiescenceViolation.SecondStfu, _channel.ChannelId));
        var channelManager = CreateChannelManager(withQuiescenceService: true);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(() =>
            channelManager.HandleChannelMessageAsync(new StfuMessage(new StfuPayload(_channel.ChannelId, true)),
                                                     CreateNegotiatedFeatures(), s_peerPubKey));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Equal(_channel.ChannelId, exception.ChannelId);
        Assert.Equal(0, _lockProvider.HeldCount);
    }

    [Fact]
    public async Task Given_NoQuiescenceServiceRegistered_When_StfuHandled_Then_WarningClosesTheConnection()
    {
        // Arrange: without quiescence we can never reply, and the sender now considers the channel quiescing; only a
        // disconnection ends that (BOLT 2 "Upon disconnection: the channel is no longer considered quiescent")
        var channelManager = CreateChannelManager(withQuiescenceService: false);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(() =>
            channelManager.HandleChannelMessageAsync(new StfuMessage(new StfuPayload(_channel.ChannelId, true)),
                                                     CreateNegotiatedFeatures(), s_peerPubKey));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Equal(_channel.ChannelId, exception.ChannelId);
    }

    [Fact]
    public async Task Given_StfuFromAnotherPeer_When_Handled_Then_ErrorAndTheServiceIsNotCalled()
    {
        // Arrange
        var channelManager = CreateChannelManager(withQuiescenceService: true);

        // Act
        await Assert.ThrowsAsync<ChannelErrorException>(() =>
            channelManager.HandleChannelMessageAsync(new StfuMessage(new StfuPayload(_channel.ChannelId, true)),
                                                     CreateNegotiatedFeatures(), s_otherPubKey));

        // Assert
        _mockQuiescenceService.Verify(
            s => s.OnStfuReceived(It.IsAny<ChannelModel>(), It.IsAny<StfuPayload>(), It.IsAny<FeatureSet>()),
            Times.Never);
    }

    private ChannelManager CreateChannelManager(bool withQuiescenceService)
    {
        var handler = new StfuMessageHandler(_mockChannelMemoryRepository.Object,
                                             new Mock<ILogger<StfuMessageHandler>>().Object,
                                             withQuiescenceService ? _mockQuiescenceService.Object : null);
        var serviceProvider = new FakeServiceProvider();
        serviceProvider.AddService(typeof(IChannelMessageHandler<StfuMessage>), handler);
        serviceProvider.AddService(typeof(IUnitOfWork), _mockUnitOfWork.Object);
        serviceProvider.AddService(typeof(ChannelDomainEventQueue), new ChannelDomainEventQueue());

        return new ChannelManager(new Mock<IBlockchainMonitor>().Object, _lockProvider,
                                  _mockChannelMemoryRepository.Object, new Mock<ILogger<ChannelManager>>().Object,
                                  new Mock<ILightningSigner>().Object, serviceProvider);
    }

    private static FeatureOptions CreateNegotiatedFeatures()
    {
        // Negotiated features come from FeatureOptions.GetNodeOptions (AllowExperimentalFeatures set)
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionQuiesce, false);
        return FeatureOptions.GetNodeOptions(features, null);
    }

    private static CompactPubKey CreateKey(byte fill)
    {
        var key = Enumerable.Repeat(fill, 33).ToArray();
        key[0] = 0x02;
        return new CompactPubKey(key);
    }

    private static ChannelModel CreateOpenChannel(byte id)
    {
        var channelIdBytes = new byte[32];
        channelIdBytes[0] = id;
        var txIdBytes = new byte[32];
        txIdBytes[0] = id;

        var fundingAmount = LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(fundingAmount, s_peerPubKey, s_peerPubKey)
        {
            TransactionId = new TxId(txIdBytes),
            Index = 0
        };
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                                     LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                                     LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_peerPubKey, s_peerPubKey, s_peerPubKey, s_peerPubKey, s_peerPubKey,
                                            s_peerPubKey);
        var commitmentNumber = new CommitmentNumber(s_peerPubKey, s_peerPubKey, new FakeSha256());

        return new ChannelModel(channelConfig, new ChannelId(channelIdBytes), commitmentNumber, fundingOutput, true,
                                null, null, LightningMoney.Zero, keySet, 0, 0, fundingAmount, keySet, 0,
                                s_peerPubKey, 0, ChannelState.Open, ChannelVersion.V1);
    }

    /// <summary>A <see cref="ChannelLockProvider"/> that reports which channel ids are held right now.</summary>
    private sealed class TrackingLockProvider : IChannelLockProvider
    {
        private readonly ChannelLockProvider _inner = new();
        private readonly Dictionary<ChannelId, int> _held = [];
        private readonly Lock _sync = new();

        public int HeldCount
        {
            get
            {
                lock (_sync)
                    return _held.Count;
            }
        }

        public bool IsHeld(ChannelId channelId)
        {
            lock (_sync)
                return _held.ContainsKey(channelId);
        }

        public async Task<IDisposable> AcquireAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        {
            var inner = await _inner.AcquireAsync(channelId, cancellationToken);
            return Track(channelId, inner);
        }

        public IDisposable Acquire(ChannelId channelId) => Track(channelId, _inner.Acquire(channelId));

        private Releaser Track(ChannelId channelId, IDisposable inner)
        {
            lock (_sync)
                _held[channelId] = _held.GetValueOrDefault(channelId) + 1;
            return new Releaser(this, channelId, inner);
        }

        private void Untrack(ChannelId channelId)
        {
            lock (_sync)
            {
                if (--_held[channelId] == 0)
                    _held.Remove(channelId);
            }
        }

        private sealed class Releaser(TrackingLockProvider owner, ChannelId channelId, IDisposable inner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                owner.Untrack(channelId);
                inner.Dispose();
            }
        }
    }
}