using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Node.Fencing;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Domain.Transport;
using Infrastructure.Node.Services;
using Infrastructure.Protocol.Services;

/// <summary>
/// NL-1343: the sends that bypass the <c>PeerOutbox</c> (our init, the keep-alive ping, the pong to a peer's ping, a
/// connection warning, the error or warning of a disconnect, a gossip query reply, a gossip sync query, the
/// <c>peer_storage_retrieval</c> reply) are fenced too, because the write fence now sits in the message service every
/// message passes. Each runs on the real <see cref="PeerService"/>, <see cref="PeerCommunicationService"/> and
/// <see cref="MessageService"/> over a recording transport: a refusal writes nothing and closes the connection.
/// </summary>
public class PeerSendWriteFenceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Given_ARefusingFence_When_OurInitIsSent_Then_NothingIsWrittenAndTheConnectionFails()
    {
        // Arrange
        var connection = new Connection { Fence = { Refuse = true } };

        // Act
        var exception = await Assert.ThrowsAsync<ConnectionException>(
                            () => connection.Communication.InitializeAsync(TimeSpan.FromSeconds(30)));

        // Assert
        Assert.Empty(connection.Written);
        Assert.IsType<NodeFencedException>(exception.InnerException?.InnerException);
        Assert.Equal([NodeEffect.PeerSend], connection.Fence.Effects);
    }

    [Fact]
    public async Task Given_AFenceThatStartsRefusing_When_ThePeerPings_Then_NoPongIsWrittenAndThePeerIsDisconnected()
    {
        // Arrange
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Deliver(connection.CreateInitMessage());
        await peer.WaitForInitAsync(TestContext.Current.CancellationToken);
        connection.Fence.Refuse = true;

        // Act
        connection.Deliver(new PingMessage(new PingPayload()));

        // Assert
        await connection.AssertDisconnectedWithoutWritingAsync();
    }

    [Fact]
    public async Task Given_AFenceThatStartsRefusing_When_TheKeepAlivePings_Then_NoPingIsWritten()
    {
        // Arrange
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Deliver(connection.CreateInitMessage());
        await peer.WaitForInitAsync(TestContext.Current.CancellationToken);
        connection.Fence.Refuse = true;

        // Act: the ping loop hands its ping over
        connection.PingPong.Raise(p => p.OnPingMessageReady += null, connection.PingPong.Object,
                                  new PingMessage(new PingPayload()));

        // Assert
        await connection.AssertDisconnectedWithoutWritingAsync();
    }

    [Fact]
    public async Task Given_ARefusingFence_When_AConnectionWarningIsSent_Then_ItIsNotWritten()
    {
        // Arrange
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Fence.Refuse = true;

        // Act
        await peer.SendWarningAsync(new WarningException("careful"));

        // Assert
        await connection.AssertDisconnectedWithoutWritingAsync();
    }

    [Fact]
    public async Task Given_ARefusingFence_When_TheConnectionClosesWithAnError_Then_TheErrorIsNotWritten()
    {
        // Arrange: an error for a channel makes the peer fail it; a fenced instance must not send one
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Fence.Refuse = true;

        // Act
        peer.Disconnect(new ChannelErrorException("fail it", new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray())));

        // Assert
        await connection.AssertDisconnectedWithoutWritingAsync();
    }

    [Fact]
    public async Task Given_AFenceThatStartsRefusing_When_ThePeerQueriesWithoutASyncService_Then_NoReplyIsWritten()
    {
        // Arrange
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Deliver(connection.CreateInitMessage());
        await peer.WaitForInitAsync(TestContext.Current.CancellationToken);
        connection.Fence.Refuse = true;

        // Act: a node without a graph answers a range query with one empty reply
        connection.Deliver(new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Regtest, 0, 100)));

        // Assert
        await connection.AssertDisconnectedWithoutWritingAsync();
    }

    [Fact]
    public async Task Given_ARefusingFence_When_TheSyncSendsAQuery_Then_ItIsNotWritten()
    {
        // Arrange: GossipSyncManager's queries and filters go out through SendGossipMessageAsync
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Fence.Refuse = true;

        // Act
        await peer.SendGossipMessageAsync(
            new GossipTimestampFilterMessage(
                new GossipTimestampFilterPayload(ChainConstants.Regtest, 0, uint.MaxValue)));

        // Assert
        await connection.AssertDisconnectedWithoutWritingAsync();
    }

    [Fact]
    public async Task Given_ARefusingFence_When_ThePeersBlobIsHandedBack_Then_TheRetrievalIsNotWritten()
    {
        // Arrange: the peer_storage_retrieval reply goes out through SendPeerStorageMessageAsync
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Fence.Refuse = true;

        // Act
        await peer.SendPeerStorageMessageAsync(
            new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(new byte[] { 1, 2, 3 })));

        // Assert
        await connection.AssertDisconnectedWithoutWritingAsync();
    }

    [Fact]
    public async Task Given_AnAllowingFence_When_TheConnectionRuns_Then_EverySendIsCheckedAndWritten()
    {
        // Arrange
        var connection = new Connection();
        var peer = connection.StartPeer();
        connection.Deliver(connection.CreateInitMessage());
        await peer.WaitForInitAsync(TestContext.Current.CancellationToken);

        // Act
        await peer.SendWarningAsync(new WarningException("careful"));
        await peer.SendPeerStorageMessageAsync(
            new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(new byte[] { 1 })));

        // Assert: our init, the warning and the retrieval, each checked once
        Assert.Equal([MessageTypes.Init, MessageTypes.Warning, MessageTypes.PeerStorageRetrieval],
                     connection.Written.Select(m => m.Type));
        Assert.Equal(3, connection.Fence.Effects.Count);
        Assert.False(connection.Disconnected.Task.IsCompleted);
    }

    /// <summary>
    /// One connection: the real message, communication and peer services over a recording transport, with a fence.
    /// </summary>
    private sealed class Connection
    {
        private readonly ConcurrentQueue<IMessage> _incoming = new();
        private readonly List<IMessage> _written = [];
        private readonly Mock<ITransportService> _transport = new();
        private readonly FeatureOptions _features = new() { ChainHashes = [ChainConstants.Regtest] };

        public Connection()
        {
            var pubKey = new byte[33];
            pubKey[0] = 0x02;
            pubKey[32] = 0x01;

            _transport.SetupGet(t => t.IsConnected).Returns(true);
            _transport.Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                      .Callback<IMessage, CancellationToken>((m, _) =>
                       {
                           lock (_written)
                               _written.Add(m);
                       })
                      .Returns(Task.CompletedTask);

            var serializer = new Mock<IMessageSerializer>();
            serializer.Setup(s => s.DeserializeMessageAsync(It.IsAny<Stream>()))
                      .ReturnsAsync(() => _incoming.TryDequeue(out var message) ? message : null);

            var messageFactory = new Mock<IMessageFactory>();
            messageFactory.Setup(f => f.CreateInitMessage(It.IsAny<AddressDescriptor?>()))
                          .Returns(() => CreateInitMessage());
            messageFactory.Setup(f => f.CreatePongMessage(It.IsAny<IMessage>()))
                          .Returns(() => new PongMessage(new PongPayload(0)));
            messageFactory.Setup(f => f.CreateWarningMessage(It.IsAny<string>(), It.IsAny<ChannelId?>()))
                          .Returns((string text, ChannelId? channelId) =>
                                       new WarningMessage(new ErrorPayload(channelId, text)));

            PingPong.Setup(p => p.StartPingAsync(It.IsAny<CancellationToken>()))
                    .Returns((CancellationToken token) => Task.Delay(Timeout.Infinite, token));

            var messageService = new MessageService(NullLogger<IMessageService>.Instance, serializer.Object,
                                                    _transport.Object, null, Fence);
            Communication = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                         messageService, messageFactory.Object,
                                                         new CompactPubKey(pubKey), PingPong.Object,
                                                         new FakeServiceProvider());
            Communication.DisconnectEvent += (_, e) => Disconnected.TrySetResult(e);
        }

        public FakeNodeWriteFence Fence { get; } = new();

        public Mock<IPingPongService> PingPong { get; } = new();

        public PeerCommunicationService Communication { get; }

        public TaskCompletionSource<Exception?> Disconnected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The messages written to the transport (our init included).</summary>
        public IReadOnlyList<IMessage> Written
        {
            get
            {
                lock (_written)
                    return _written.ToList();
            }
        }

        /// <summary>Starts the peer service, which sends our init (unless the fence refuses already).</summary>
        public PeerService StartPeer() =>
            new(Communication, _features, NullLogger<PeerService>.Instance, TimeSpan.FromSeconds(30));

        public InitMessage CreateInitMessage() =>
            new(new InitPayload(_features.GetNodeFeatures()), new NetworksTlv([ChainConstants.Regtest]));

        /// <summary>The peer sends <paramref name="message"/> (through the serializer and the transport).</summary>
        public void Deliver(IMessage message)
        {
            _incoming.Enqueue(message);
            _transport.Raise(t => t.MessageReceived += null, _transport.Object, new MemoryStream());
        }

        /// <summary>
        /// The connection closed because the fence refused, and nothing but what was written before the refusal (our
        /// init at most) reached the transport.
        /// </summary>
        public async Task AssertDisconnectedWithoutWritingAsync()
        {
            await Disconnected.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
            Assert.All(Written, m => Assert.Equal(MessageTypes.Init, m.Type));
            Assert.Contains(NodeEffect.PeerSend, Fence.Effects);
        }
    }
}