using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Tests.Utils;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Transport.Services;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Domain.Transport;
using Exceptions;
using Infrastructure.Protocol.Constants;
using Infrastructure.Transport.Interfaces;
using Infrastructure.Transport.Services;

// ReSharper disable AccessToDisposedClosure
public class TransportServiceTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    [Fact]
    public async Task
        Given_TransportServiceAsInitiator_When_InitializeIsCalled_Then_HandshakeServicePerformStepIsCalledTwice()
    {
        // Given
        var handshakeServiceMock = new Mock<FakeHandshakeService>();
        handshakeServiceMock.Object.SetIsInitiator(true);
        var messageSerializerMock = new Mock<IMessageSerializer>();
        var availablePort = await PortPoolUtil.GetAvailablePortAsync();
        var tcpListener = new TcpListener(IPAddress.Loopback, availablePort);
        tcpListener.Start();

        try
        {
            var steps = 2;
            handshakeServiceMock
               .Setup(x => x.PerformStep(It.IsAny<byte[]>(), out It.Ref<byte[]>.IsAny))
               .Returns((byte[] inMessage, out byte[] outMessage) =>
                {
                    ITransport? transport = null;
                    switch (steps)
                    {
                        case 2:
                            {
                                steps--;
                                if (inMessage.Length != 50 && inMessage.Length != 0)
                                {
                                    throw new InvalidOperationException("Expected 50 bytes");
                                }

                                outMessage = new byte[50];
                                return (50, transport);
                            }
                        case 1:
                            {
                                steps--;
                                if (inMessage.Length != 50 && inMessage.Length != 66 && inMessage.Length != 0)
                                {
                                    throw new InvalidOperationException("Expected 66 bytes");
                                }

                                outMessage = new byte[66];

                                return (66, new FakeTransport());
                            }
                        default:
                            throw new InvalidOperationException("There's no more steps to complete");
                    }
                });
            var tcpClient1 = new TcpClient();
            var acceptTask = Task.Run(async () =>
            {
                var tcpClient2 = await tcpListener.AcceptTcpClientAsync();
                var stream = tcpClient2.GetStream();
                var buffer = new byte[50];
                await stream.ReadExactlyAsync(buffer);

                await stream.WriteAsync(buffer);

                buffer = new byte[66];
                await stream.ReadExactlyAsync(buffer);
            }, TestContext.Current.CancellationToken);
            await tcpClient1.ConnectAsync(IPEndPoint.Parse(tcpListener.LocalEndpoint.ToEndpointString()),
                                          TestContext.Current.CancellationToken);
            var transportService = new TransportService(_mockLogger.Object, messageSerializerMock.Object,
                                                        TimeSpan.FromSeconds(30), handshakeServiceMock.Object,
                                                        tcpClient1);

            // When
            await transportService.InitializeAsync();
            await acceptTask;

            // Then
            handshakeServiceMock.Verify(x => x.PerformStep(It.IsAny<byte[]>(), out It.Ref<byte[]>.IsAny),
                                        Times.Exactly(2));
        }
        finally
        {
            tcpListener.Dispose();
            PortPoolUtil.ReleasePort(availablePort);
        }
    }

    [Fact]
    public async Task
        Given_TransportServiceAsInitiator_When_InitializeIsCalledAndTcpClinetIsDisconnected_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var handshakeServiceMock = new Mock<FakeHandshakeService>();
        var messageSerializerMock = new Mock<IMessageSerializer>();
        var tcpClient1 = new TcpClient();
        var transportService = new TransportService(_mockLogger.Object, messageSerializerMock.Object,
                                                    TimeSpan.FromSeconds(30), handshakeServiceMock.Object, tcpClient1);

        // Act
        var exception = await Assert
                           .ThrowsAnyAsync<InvalidOperationException>(() => transportService.InitializeAsync());

        // Assert
        Assert.Equal("TcpClient is not connected", exception.Message);
    }

    [Fact]
    public async Task Given_TransportService_When_TimeoutOccurs_Then_ThrowsConnectionTimeoutException()
    {
        // Arrange
        var handshakeServiceMock = new Mock<FakeHandshakeService>();
        handshakeServiceMock.Object.SetIsInitiator(true);
        var messageSerializerMock = new Mock<IMessageSerializer>();
        var availablePort = await PortPoolUtil.GetAvailablePortAsync();
        var tcpListener = new TcpListener(IPAddress.Loopback, availablePort);
        tcpListener.Start();

        try
        {
            var steps = 2;
            handshakeServiceMock
               .Setup(x => x.PerformStep(It.IsAny<byte[]>(), out It.Ref<byte[]>.IsAny))
               .Returns((byte[] inMessage, out byte[] outMessage) =>
                {
                    if (steps != 2)
                    {
                        throw new InvalidOperationException("There's no more steps to complete");
                    }

                    steps--;
                    if (inMessage.Length != 50 && inMessage.Length != 0)
                    {
                        throw new InvalidOperationException("Expected 50 bytes");
                    }

                    outMessage = new byte[50];
                    ITransport? transport = null;
                    return (50, transport);
                });
            var tcpClient1 = new TcpClient();
            var acceptTask = Task.Run(async () =>
            {
                var tcpClient2 = await tcpListener.AcceptTcpClientAsync();
                var stream = tcpClient2.GetStream();
                var buffer = new byte[50];
                await stream.ReadExactlyAsync(buffer);

                await stream.WriteAsync(buffer);
            }, TestContext.Current.CancellationToken);
            await tcpClient1.ConnectAsync(IPEndPoint.Parse(tcpListener.LocalEndpoint.ToEndpointString()),
                                          TestContext.Current.CancellationToken);
            var transportService = new TransportService(_mockLogger.Object, messageSerializerMock.Object, TimeSpan.Zero,
                                                        handshakeServiceMock.Object, tcpClient1);

            // Act
            var exception = await Assert
                               .ThrowsAnyAsync<ConnectionTimeoutException>(() => transportService.InitializeAsync());
            await acceptTask;

            // Assert
            Assert.Contains("Timeout while reading Handshake's Act 2 from host", exception.Message);
        }
        finally
        {
            tcpListener.Dispose();
            PortPoolUtil.ReleasePort(availablePort);
        }
    }

    [Fact]
    public async Task Given_PeerSendsFrameInSmallTcpChunks_When_Reading_Then_MessageIsReceived()
    {
        // Arrange
        var payload = Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray();
        var frame = FramingTransport.BuildFrame(payload, 0);
        using var connection = await ConnectedTransportService.CreateAsync(new FramingTransport(),
                                                                           new Mock<IMessageSerializer>().Object);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Service.MessageReceived += (_, stream) => received.TrySetResult(stream.ToArray());
        connection.Service.ExceptionRaised += (_, e) => received.TrySetException(e);
        var peerStream = connection.Peer.GetStream();

        // Act - header and body both arrive split across several TCP segments
        foreach (var (start, end) in new[] { (0, 5), (5, 18), (18, 700), (700, frame.Length) })
        {
            await peerStream.WriteAsync(frame.AsMemory(start, end - start), TestContext.Current.CancellationToken);
            await peerStream.FlushAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task Given_ConcurrentSenders_When_WritingMessages_Then_FramesAreWrittenInEncryptionOrder()
    {
        // Arrange
        var transport = new FramingTransport { HoldFirstWrite = true };
        var serializerMock = new Mock<IMessageSerializer>();
        serializerMock.Setup(x => x.SerializeAsync(It.IsAny<IMessage>(), It.IsAny<Stream>()))
                      .Returns((IMessage _, Stream stream) => stream.WriteAsync(new byte[] { 1, 2, 3, 4 }).AsTask());
        using var connection = await ConnectedTransportService.CreateAsync(transport, serializerMock.Object);
        var message = new Mock<IMessage>().Object;

        // Act
        var first = Task.Run(() => connection.Service.WriteMessageAsync(message),
                             TestContext.Current.CancellationToken);
        await transport.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10),
                                                         TestContext.Current.CancellationToken);
        var second = Task.Run(() => connection.Service.WriteMessageAsync(message),
                              TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var peerStream = connection.Peer.GetStream();
        var sequences = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            var header = new byte[ProtocolConstants.MessageHeaderSize];
            await peerStream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            sequences.Add(BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(2, 4)));
            var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header) + 16];
            await peerStream.ReadExactlyAsync(body, TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal([0, 1], sequences);
    }

    [Fact]
    public async Task Given_MaximumSizeMessage_When_Writing_Then_FullFrameIsSent()
    {
        // Arrange
        var payload = Enumerable.Range(0, ProtocolConstants.MaxMessageLength).Select(i => (byte)i).ToArray();
        var serializerMock = new Mock<IMessageSerializer>();
        serializerMock.Setup(x => x.SerializeAsync(It.IsAny<IMessage>(), It.IsAny<Stream>()))
                      .Returns((IMessage _, Stream stream) => stream.WriteAsync(payload).AsTask());
        using var connection =
            await ConnectedTransportService.CreateAsync(new FramingTransport(), serializerMock.Object);

        // Act
        await connection.Service.WriteMessageAsync(new Mock<IMessage>().Object, TestContext.Current.CancellationToken);
        var frame = new byte[ProtocolConstants.MessageHeaderSize + ProtocolConstants.MaxMessageLength + 16];
        await connection.Peer.GetStream().ReadExactlyAsync(frame, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FramingTransport.BuildFrame(payload, 0), frame);
    }

    [Fact]
    public async Task Given_PeerSendsMaximumSizeMessage_When_Reading_Then_MessageIsReceived()
    {
        // Arrange
        var payload = Enumerable.Range(0, ProtocolConstants.MaxMessageLength).Select(i => (byte)(i * 7)).ToArray();
        using var connection = await ConnectedTransportService.CreateAsync(new FramingTransport(),
                                                                           new Mock<IMessageSerializer>().Object);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Service.MessageReceived += (_, stream) => received.TrySetResult(stream.ToArray());
        connection.Service.ExceptionRaised += (_, e) => received.TrySetException(e);

        // Act
        await connection.Peer.GetStream().WriteAsync(FramingTransport.BuildFrame(payload, 0),
                                                     TestContext.Current.CancellationToken);
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(payload, result);
    }

    /// <summary>
    /// Plaintext BOLT 8-shaped framing: header = 2-byte length, 4-byte sequence number, 12 zero bytes;
    /// body = payload followed by a 16-byte zero "MAC". The sequence number stands in for the nonce.
    /// </summary>
    private sealed class FramingTransport : ITransport
    {
        private readonly ManualResetEventSlim _secondWriteEntered = new();
        private int _nextSequence;

        public bool HoldFirstWrite { get; init; }

        public TaskCompletionSource FirstWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static byte[] BuildFrame(ReadOnlySpan<byte> payload, int sequence)
        {
            var frame = new byte[ProtocolConstants.MessageHeaderSize + payload.Length + 16];
            BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(2), sequence);
            payload.CopyTo(frame.AsSpan(ProtocolConstants.MessageHeaderSize));
            return frame;
        }

        public int WriteMessage(ReadOnlySpan<byte> payload, Span<byte> messageBuffer)
        {
            var sequence = Interlocked.Increment(ref _nextSequence) - 1;
            if (HoldFirstWrite)
            {
                if (sequence == 0)
                {
                    // Give a concurrent sender the chance to encrypt (and write) before this one finishes
                    FirstWriteEntered.TrySetResult();
                    if (_secondWriteEntered.Wait(TimeSpan.FromMilliseconds(500)))
                        Thread.Sleep(200);
                }
                else
                {
                    _secondWriteEntered.Set();
                }
            }

            var frame = BuildFrame(payload, sequence);
            if (frame.Length > messageBuffer.Length)
                throw new ArgumentException("Message buffer does not have enough space to hold the ciphertext.");

            frame.CopyTo(messageBuffer);
            return frame.Length;
        }

        public int ReadMessageLength(ReadOnlySpan<byte> lc) => BinaryPrimitives.ReadUInt16BigEndian(lc) + 16;

        public int ReadMessagePayload(ReadOnlySpan<byte> message, Span<byte> payloadBuffer)
        {
            message[..^16].CopyTo(payloadBuffer);
            return message.Length - 16;
        }

        public void Dispose()
        {
            _secondWriteEntered.Dispose();
        }
    }

    private sealed class ScriptedHandshakeService(ITransport transport) : IHandshakeService
    {
        private int _step;

        public bool IsInitiator => true;
        public CompactPubKey? RemoteStaticPublicKey { get; } = new Key().PubKey.ToBytes();

        public int PerformStep(ReadOnlySpan<byte> inMessage, Span<byte> outMessage, out ITransport? outTransport)
        {
            _step++;
            outTransport = _step == 2 ? transport : null;
            return _step == 1 ? 50 : 66;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConnectedTransportService : IDisposable
    {
        private readonly TcpListener _listener;

        public TransportService Service { get; }
        public TcpClient Peer { get; }

        private ConnectedTransportService(TcpListener listener, TransportService service, TcpClient peer)
        {
            _listener = listener;
            Service = service;
            Peer = peer;
        }

        public static async Task<ConnectedTransportService> CreateAsync(ITransport transport,
                                                                        IMessageSerializer serializer)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var acceptTask = Task.Run(async () =>
            {
                var peer = await listener.AcceptTcpClientAsync();
                peer.NoDelay = true;
                var stream = peer.GetStream();
                var buffer = new byte[66];
                await stream.ReadExactlyAsync(buffer.AsMemory(0, 50));
                await stream.WriteAsync(buffer.AsMemory(0, 50));
                await stream.ReadExactlyAsync(buffer.AsMemory(0, 66));
                return peer;
            });

            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var service = new TransportService(new Mock<ILogger>().Object, serializer, TimeSpan.FromSeconds(30),
                                               new ScriptedHandshakeService(transport), client);
            await service.InitializeAsync();

            return new ConnectedTransportService(listener, service, await acceptTask);
        }

        public void Dispose()
        {
            Service.Dispose();
            Peer.Dispose();
            _listener.Dispose();
        }
    }
}
// ReSharper restore AccessToDisposedClosure