using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Addresses;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Node.Services;

/// <summary>
/// The graph gossip arms of <see cref="PeerService"/> (BOLT7 plan G2-T4), the gossip_timestamp_filter bootstrap and
/// the init remote_addr handling (NL-344).
/// </summary>
public class PeerServiceGossipTests
{
    private readonly Mock<IPeerCommunicationService> _communication = new();
    private readonly FeatureOptions _features = new() { ChainHashes = [ChainConstants.Regtest] };
    private readonly Mock<IGossipIngress> _ingress = new();

    public PeerServiceGossipTests()
    {
        var pubKey = new byte[33];
        pubKey[0] = 0x02;
        _communication.SetupGet(x => x.PeerCompactPubKey).Returns(new CompactPubKey(pubKey));
        _communication.Setup(x => x.SendMessageAsync(It.IsAny<IMessage>())).Returns(Task.CompletedTask);
        _ingress.SetupGet(i => i.IsEnabled).Returns(true);
        _ingress.Setup(i => i.TryEnqueue(It.IsAny<IPeerService>(), It.IsAny<IMessage>())).Returns(true);
    }

    public static TheoryData<IMessage> GraphMessages => new()
    {
        new ChannelAnnouncementMessage(
            new ChannelAnnouncementPayload(ChannelAnnouncementPayload.EmptySignature,
                                           ChannelAnnouncementPayload.EmptySignature,
                                           ChannelAnnouncementPayload.EmptySignature,
                                           ChannelAnnouncementPayload.EmptySignature, ReadOnlyMemory<byte>.Empty,
                                           ChainConstants.Regtest, new ShortChannelId(103, 1, 0), CreateKey(2),
                                           CreateKey(3), CreateKey(4), CreateKey(5))),
        new NodeAnnouncementMessage(
            new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature, ReadOnlyMemory<byte>.Empty,
                                        1_700_000_000, CreateKey(2), new byte[3],
                                        NodeAnnouncementPayload.EncodeAlias("alias"), ReadOnlyMemory<byte>.Empty))
    };

    [Theory]
    [MemberData(nameof(GraphMessages))]
    public void Given_AnIngress_When_AnAnnouncementArrives_Then_ItIsQueuedWithThePeerAsOrigin(IMessage message)
    {
        // Arrange
        var peerService = CreatePeerService(_ingress.Object);
        RaiseMessage(CreateInitMessage());

        // Act
        RaiseMessage(message);

        // Assert
        _ingress.Verify(i => i.TryEnqueue(peerService, message), Times.Once);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_AnIngress_When_AChannelUpdateArrives_Then_ItIsRaisedAndQueued()
    {
        // Arrange
        var peerService = CreatePeerService(_ingress.Object);
        RaiseMessage(CreateInitMessage());
        ChannelUpdateMessage? raised = null;
        peerService.OnChannelUpdateReceived += (_, m) => raised = m;
        var update = new ChannelUpdateMessage(
            new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest,
                                     new ShortChannelId(103, 1, 0), 1_700_000_000,
                                     ChannelUpdatePayload.MessageFlagMustBeOne, 0, 40, 1_000, 1_000, 1,
                                     990_000_000));

        // Act
        RaiseMessage(update);

        // Assert: our own channels keep the W1-E path; the graph gets it too
        Assert.Same(update, raised);
        _ingress.Verify(i => i.TryEnqueue(peerService, update), Times.Once);
    }

    [Fact]
    public void Given_AnIngressThatRefuses_When_AnAnnouncementArrives_Then_ItIsDroppedAndThePeerStays()
    {
        // Arrange: a full queue or a disabled graph
        _ingress.Setup(i => i.TryEnqueue(It.IsAny<IPeerService>(), It.IsAny<IMessage>())).Returns(false);
        CreatePeerService(_ingress.Object);
        RaiseMessage(CreateInitMessage());

        // Act
        RaiseMessage(new NodeAnnouncementMessage(
            new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature, ReadOnlyMemory<byte>.Empty,
                                        1_700_000_000, CreateKey(2), new byte[3],
                                        NodeAnnouncementPayload.EncodeAlias("alias"), ReadOnlyMemory<byte>.Empty)));

        // Assert
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_GraphEnabledAndGossipQueriesNegotiated_When_InitIsAccepted_Then_AFullTimestampFilterIsSent()
    {
        // Arrange
        CreatePeerService(_ingress.Object);

        // Act
        RaiseMessage(CreateInitMessage());

        // Assert: first_timestamp 0 and the whole range, so LND dumps its graph (plan G0-T5)
        _communication.Verify(x => x.SendMessageAsync(It.Is<IMessage>(m =>
                                  m is GossipTimestampFilterMessage
                               && ((GossipTimestampFilterMessage)m).Payload.ChainHash == ChainConstants.Regtest
                               && ((GossipTimestampFilterMessage)m).Payload.FirstTimestamp == 0
                               && ((GossipTimestampFilterMessage)m).Payload.TimestampRange == uint.MaxValue)),
                              Times.Once);
    }

    [Fact]
    public void Given_PeerInitHandledWhileOursIsBeingSent_When_OursGoesOut_Then_TheFilterFollowsIt()
    {
        // Arrange: the read loop hands us the peer's init before our own init finished writing (seen against LND:
        // "very first message between nodes must be init message")
        var events = new List<string>();
        _communication.Setup(x => x.InitializeAsync(It.IsAny<TimeSpan>()))
                      .Returns(() =>
                       {
                           RaiseMessage(CreateInitMessage());
                           events.Add("our init sent");
                           return Task.CompletedTask;
                       });
        _communication.Setup(x => x.SendMessageAsync(It.IsAny<IMessage>()))
                      .Callback<IMessage, CancellationToken>((m, _) => events.Add(m.Type.ToString()))
                      .Returns(Task.CompletedTask);

        // Act
        CreatePeerService(_ingress.Object);

        // Assert
        Assert.Equal(["our init sent", nameof(MessageTypes.GossipTimestampFilter)], events);
    }

    [Fact]
    public void Given_GraphDisabled_When_InitIsAccepted_Then_NoFilterIsSent()
    {
        // Arrange: mainnet by default (plan D12)
        _ingress.SetupGet(i => i.IsEnabled).Returns(false);
        CreatePeerService(_ingress.Object);

        // Act
        RaiseMessage(CreateInitMessage());

        // Assert
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<GossipTimestampFilterMessage>()), Times.Never);
    }

    [Fact]
    public void Given_PeerWithoutGossipQueries_When_InitIsAccepted_Then_NoFilterIsSent()
    {
        // Arrange
        CreatePeerService(_ingress.Object);
        var peerFeatures = new FeatureOptions { GossipQueries = FeatureSupport.No };

        // Act
        RaiseMessage(new InitMessage(new InitPayload(peerFeatures.GetNodeFeatures()),
                                     new NetworksTlv([ChainConstants.Regtest])));

        // Assert
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<GossipTimestampFilterMessage>()), Times.Never);
    }

    [Fact]
    public void Given_NoIngress_When_InitIsAccepted_Then_NoFilterIsSent()
    {
        // Arrange
        CreatePeerService(null);

        // Act
        RaiseMessage(CreateInitMessage());

        // Assert
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<GossipTimestampFilterMessage>()), Times.Never);
    }

    [Fact]
    public async Task Given_InitWithRemoteAddr_When_InitIsAccepted_Then_ItIsOurObservedAddressNotThePeersAddress()
    {
        // Arrange: NL-344, BOLT 1 remote_addr is the address the peer sees us at
        var peerService = CreatePeerService(null);
        var remoteAddress = new RemoteAddressTlv(1, "203.0.113.7", 9735);

        // Act
        RaiseMessage(CreateInitMessage(remoteAddress));
        await peerService.WaitForInitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(peerService.ObservedAddress);
        Assert.Equal(AddressDescriptorType.IPv4, peerService.ObservedAddress.Type);
        Assert.Equal("203.0.113.7", peerService.ObservedAddress.Host);
        Assert.Equal((ushort)9735, peerService.ObservedAddress.Port);
    }

    [Fact]
    public async Task Given_InitWithUndecodableRemoteAddr_When_InitIsAccepted_Then_InitSucceedsAndTheAddressIsDropped()
    {
        // Arrange: NL-344, the odd advisory TLV never fails the init
        var peerService = CreatePeerService(null);
        var init = new InitMessage(new InitPayload(_features.GetNodeFeatures()),
                                   new NetworksTlv([ChainConstants.Regtest]))
        {
            UndecodableRemoteAddress = [4, 1, 2, 3]
        };

        // Act
        RaiseMessage(init);
        await peerService.WaitForInitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(peerService.ObservedAddress);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    public static TheoryData<IMessage> QueryMessages => new()
    {
        new QueryShortChannelIdsMessage(new QueryShortChannelIdsPayload(ChainConstants.Regtest, new byte[] { 0 })),
        new ReplyShortChannelIdsEndMessage(new ReplyShortChannelIdsEndPayload(ChainConstants.Regtest, true)),
        new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Regtest, 0, 100)),
        new ReplyChannelRangeMessage(new ReplyChannelRangePayload(ChainConstants.Regtest, 0, 100, true,
                                                                  new byte[] { 0 })),
        new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 0, 10))
    };

    [Theory]
    [MemberData(nameof(QueryMessages))]
    public void Given_ASyncService_When_AQueryMessageArrives_Then_ItGoesToTheSyncServiceOnly(IMessage message)
    {
        // Arrange: G3-T1/G3-T2, messages 261-265
        var sync = new Mock<IGossipSyncService>();
        var peerService = CreatePeerService(_ingress.Object, sync.Object);
        RaiseMessage(CreateInitMessage());
        _communication.Invocations.Clear();

        // Act
        RaiseMessage(message);

        // Assert: nothing answered here, the connection stays
        sync.Verify(s => s.HandleMessage(peerService, message), Times.Once);
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<IMessage>()), Times.Never);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_ASyncService_When_InitIsAccepted_Then_ItDecidesAndNoBootstrapFilterIsSent()
    {
        // Arrange: G3-T2 replaces the wave G-B bootstrap filter (0, 0xFFFFFFFF)
        var sync = new Mock<IGossipSyncService>();
        var peerService = CreatePeerService(_ingress.Object, sync.Object);

        // Act
        RaiseMessage(CreateInitMessage());

        // Assert
        sync.Verify(s => s.OnPeerInitialized(peerService), Times.Once);
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<GossipTimestampFilterMessage>()), Times.Never);
    }

    [Fact]
    public void Given_ASyncServiceAndPeerInitHandledWhileOursIsBeingSent_When_OursGoesOut_Then_TheSyncStartsAfterIt()
    {
        // Arrange: BOLT 1, init first
        var events = new List<string>();
        var sync = new Mock<IGossipSyncService>();
        sync.Setup(s => s.OnPeerInitialized(It.IsAny<IPeerService>())).Callback(() => events.Add("sync"));
        _communication.Setup(x => x.InitializeAsync(It.IsAny<TimeSpan>()))
                      .Returns(() =>
                       {
                           RaiseMessage(CreateInitMessage());
                           events.Add("our init sent");
                           return Task.CompletedTask;
                       });

        // Act
        CreatePeerService(_ingress.Object, sync.Object);

        // Assert
        Assert.Equal(["our init sent", "sync"], events);
    }

    [Fact]
    public void Given_NoSyncService_When_QueryChannelRangeArrives_Then_OneFinalEmptyReplyCoversTheRange()
    {
        // Arrange
        CreatePeerService(null);
        RaiseMessage(CreateInitMessage());

        // Act
        RaiseMessage(new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Regtest, 42, 0)));

        // Assert: BOLT 7, first + number > the query's first even for number_of_blocks 0
        _communication.Verify(x => x.SendMessageAsync(It.Is<IMessage>(m =>
                                  m is ReplyChannelRangeMessage
                               && ((ReplyChannelRangeMessage)m).Payload.FirstBlocknum == 42
                               && ((ReplyChannelRangeMessage)m).Payload.NumberOfBlocks == 1
                               && ((ReplyChannelRangeMessage)m).Payload.SyncComplete)), Times.Once);
    }

    [Fact]
    public void Given_NoSyncService_When_QueryWithZlibEncodingArrives_Then_AWarningIsSentAndNoReply()
    {
        // Arrange: encoding 1 MUST NOT be used (plan D5)
        CreatePeerService(null);
        RaiseMessage(CreateInitMessage());

        // Act
        RaiseMessage(new QueryShortChannelIdsMessage(
                         new QueryShortChannelIdsPayload(ChainConstants.Regtest, new byte[] { 1, 0x78, 0x9c })));

        // Assert
        _communication.Verify(x => x.SendWarningAsync(It.IsAny<Domain.Exceptions.WarningException>()), Times.Once);
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<ReplyShortChannelIdsEndMessage>()), Times.Never);
    }

    private PeerService CreatePeerService(IGossipIngress? ingress, IGossipSyncService? sync = null) =>
        new(_communication.Object, _features, NullLogger<PeerService>.Instance, TimeSpan.FromSeconds(1), ingress,
            sync);

    private void RaiseMessage(IMessage message) =>
        _communication.Raise(x => x.MessageReceived += null, _communication.Object, message);

    private InitMessage CreateInitMessage(RemoteAddressTlv? remoteAddress = null) =>
        new(new InitPayload(_features.GetNodeFeatures()), new NetworksTlv([ChainConstants.Regtest]), remoteAddress);

    private static CompactPubKey CreateKey(byte fill)
    {
        var key = Enumerable.Repeat(fill, 33).ToArray();
        key[0] = 0x02;
        return new CompactPubKey(key);
    }
}