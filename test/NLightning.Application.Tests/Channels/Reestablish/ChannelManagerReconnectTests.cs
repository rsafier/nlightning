using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Reestablish;

using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT2 plan N7-T2: the <see cref="ChannelManager"/> lifecycle hooks (B2-RE-05..07) with mocked collaborators.
/// </summary>
public class ChannelManagerReconnectTests
{
    private static readonly CompactPubKey s_peer = NormalOperationTestContext.PeerNodeId;
    private static readonly CompactPubKey s_otherPeer = NormalOperationTestContext.Point(0x66);

    private readonly List<ChannelModel> _channels = [];
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IMessageSerializer> _serializer = new();
    private readonly Mock<ILightningSigner> _signer = new();
    private readonly Mock<IPeerLivenessProbe> _probe = new();
    private readonly ReestablishTracker _tracker = new();
    private readonly List<(CompactPubKey Peer, IChannelMessage Message)> _raised = [];

    public ChannelManagerReconnectTests()
    {
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel>.IsAny!))
               .Returns(new TryGetChannelDelegate((ChannelId id, out ChannelModel channel) =>
                {
                    channel = _channels.FirstOrDefault(c => c.ChannelId == id)!;
                    return channel is not null;
                }));
        _memory.Setup(m => m.TryGetChannelState(It.IsAny<ChannelId>(), out It.Ref<ChannelState>.IsAny))
               .Returns(new TryGetStateDelegate((ChannelId id, out ChannelState state) =>
                {
                    var channel = _channels.FirstOrDefault(c => c.ChannelId == id);
                    state = channel?.State ?? ChannelState.None;
                    return channel is not null;
                }));
        _signer.Setup(s => s.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
               .Returns((ChannelId _, ulong n) => NormalOperationTestContext.Point((byte)(0x40 + n)));
    }

    private delegate bool TryGetChannelDelegate(ChannelId channelId, out ChannelModel channel);

    private delegate bool TryGetStateDelegate(ChannelId channelId, out ChannelState state);

    [Fact]
    public async Task Given_TwoOpenChannelsAndAFailedOne_When_PeerConnects_Then_TwoReestablishSentAndTheErrorReturned()
    {
        // Arrange (B2-RE-06: one per channel; B2-RE-05: a failed channel re-sends its stored error instead)
        _channels.Add(CreateChannel(0x01, ChannelState.Open, s_peer));
        _channels.Add(CreateChannel(0x02, ChannelState.Open, s_peer));
        var failed = CreateChannel(0x03, ChannelState.Failed, s_peer);
        failed.MarkErrorSent(new byte[] { 0x00, 0x11 });
        _channels.Add(failed);
        _channels.Add(CreateChannel(0x04, ChannelState.Open, s_otherPeer));
        var storedError = new ErrorMessage(new ErrorPayload(failed.ChannelId, "we failed it earlier"));
        _serializer.Setup(s => s.DeserializeMessageAsync(It.IsAny<Stream>())).ReturnsAsync(storedError);
        var manager = CreateManager();

        // Act
        var errors = await manager.OnPeerConnectedAsync(s_peer);

        // Assert
        Assert.Same(storedError, Assert.Single(errors));
        Assert.Equal(2, _raised.Count);
        Assert.All(_raised, r => Assert.Equal(s_peer, r.Peer));
        var reestablishes = _raised.Select(r => Assert.IsType<ChannelReestablishMessage>(r.Message)).ToList();
        Assert.Equal([_channels[0].ChannelId, _channels[1].ChannelId],
                     reestablishes.Select(m => m.Payload.ChannelId).ToList());
        Assert.All(reestablishes, m => Assert.Equal(1UL, m.Payload.NextCommitmentNumber));
        Assert.Equal(ReestablishStatus.Sent, _tracker.GetStatus(_channels[0].ChannelId));
        Assert.Equal(ReestablishStatus.Awaiting, _tracker.GetStatus(_channels[3].ChannelId));
    }

    [Fact]
    public async Task Given_OpenChannelNotReestablished_When_PeerSendsAnUpdate_Then_WarningAndClose()
    {
        // Arrange (B2-RE-07)
        var channel = CreateChannel(0x01, ChannelState.Open, s_peer);
        _channels.Add(channel);
        var manager = CreateManager();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => manager.HandleChannelMessageAsync(
                                new NormalOperationTestContext().MessageFactory.CreateUpdateAddHtlcMessage(
                                    channel.ChannelId, 0, 1_000_000,
                                    NormalOperationTestContext.HashOf(NormalOperationTestContext.SecretOf(1)), 500,
                                    NormalOperationTestContext.Onion), new FeatureOptions(), s_peer));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Equal(channel.ChannelId, exception.ChannelId);
        Assert.Empty(_raised);
    }

    [Fact]
    public void Given_ReestablishedChannel_When_PeerConnectionChanges_Then_ItIsNoLongerReestablished()
    {
        // Arrange
        var channel = CreateChannel(0x01, ChannelState.Open, s_peer);
        _channels.Add(channel);
        _tracker.MarkOpened(channel.ChannelId, s_peer);
        var manager = CreateManager();

        // Act
        manager.OnPeerConnectionChanged(s_peer);

        // Assert
        Assert.False(_tracker.IsReestablished(channel.ChannelId));
    }

    [Fact]
    public async Task Given_TheReestablishFinished_When_Handled_Then_TheLinkIsMarkedUpOnThisConnection()
    {
        // Arrange - a channel without HTLC state (no retransmission), our reestablish already sent
        var channel = CreateChannel(0x01, ChannelState.Open, s_peer);
        _channels.Add(channel);
        var manager = CreateManager();
        await manager.OnPeerConnectedAsync(s_peer);

        // Act
        await manager.HandleChannelMessageAsync(PeerReestablish(channel.ChannelId, 1, 0), new FeatureOptions(),
                                                s_peer);

        // Assert
        Assert.True(_tracker.IsReestablished(channel.ChannelId));
        _probe.Verify(p => p.MarkLinkUp(channel.ChannelId, s_peer), Times.Once);
    }

    [Fact]
    public async Task Given_TheConnectionChangedDuringTheReestablish_When_ItFinishes_Then_TheLinkStaysDown()
    {
        // Arrange - the connection is replaced while the peer's reestablish is being handled
        var channel = CreateChannel(0x01, ChannelState.Open, s_peer);
        _channels.Add(channel);
        var manager = CreateManager();
        await manager.OnPeerConnectedAsync(s_peer);
        _raised.Clear();
        _tracker.ResetPeer(s_peer);

        // Act - the handler sends ours again (nothing went out on the new connection yet)
        await manager.HandleChannelMessageAsync(PeerReestablish(channel.ChannelId, 1, 0), new FeatureOptions(),
                                                s_peer);

        // Assert - reestablished on the connection that got ours now
        Assert.IsType<ChannelReestablishMessage>(_raised[0].Message);
        Assert.True(_tracker.IsReestablished(channel.ChannelId));
    }

    private ChannelManager CreateManager()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_tracker);
        services.AddSingleton(_serializer.Object);
        services.AddSingleton(_probe.Object);
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddScoped(_ => new Mock<IUnitOfWork>().Object);
        services.AddScoped<ReestablishService>(sp => new ReestablishService(
                                                   _signer.Object, NullLogger<ReestablishService>.Instance,
                                                   new NormalOperationTestContext().MessageFactory,
                                                   new Mock<IRevocationVerifier>().Object,
                                                   new NormalOperationTestContext().CreateTransitions()));
        services.AddScoped<Application.Channels.Handlers.Interfaces.IChannelMessageHandler<ChannelReestablishMessage>>(
            sp => new Application.Channels.Handlers.ChannelReestablishMessageHandler(
                _memory.Object, _signer.Object,
                NullLogger<Application.Channels.Handlers.ChannelReestablishMessageHandler>.Instance,
                new NormalOperationTestContext().MessageFactory, _serializer.Object,
                sp.GetRequiredService<ReestablishService>(), _tracker,
                new NormalOperationTestContext().CreateTransitions(), new Mock<IUnitOfWork>().Object));

        var manager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, new ChannelLockProvider(),
                                         _memory.Object, NullLogger<ChannelManager>.Instance, _signer.Object,
                                         services.BuildServiceProvider());
        manager.OnResponseMessageReady += (_, args) => _raised.Add((args.PeerPubKey, args.ResponseMessage));
        return manager;
    }

    private static ChannelReestablishMessage PeerReestablish(ChannelId channelId, ulong next, ulong revocation) =>
        new(new ChannelReestablishPayload(channelId, NormalOperationTestContext.Point(0x30), next, revocation,
                                          new byte[32]));

    /// <summary>A channel without a commitment snapshot (numbers 0/0), so reestablish needs nothing else.</summary>
    private static ChannelModel CreateChannel(byte tag, ChannelState state, CompactPubKey peer)
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000),
                                                  NormalOperationTestContext.Point(0x01),
                                                  NormalOperationTestContext.Point(0x02))
        {
            TransactionId = new TxId(Enumerable.Repeat(tag, 32).ToArray()),
            Index = 0
        };
        var keySet = new ChannelKeySetModel(0, NormalOperationTestContext.Point(0x01),
                                            NormalOperationTestContext.Point(0x03),
                                            NormalOperationTestContext.Point(0x04),
                                            NormalOperationTestContext.Point(0x05),
                                            NormalOperationTestContext.Point(0x06),
                                            NormalOperationTestContext.Point(0x07));
        return new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null,
                                fundingOutput, true, null, null, LightningMoney.Satoshis(800_000), keySet, 0, 0,
                                LightningMoney.Satoshis(200_000), keySet, 0, peer, 0, state, ChannelVersion.V1);
    }
}