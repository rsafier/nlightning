namespace NLightning.Domain.Transport;

using Crypto.ValueObjects;
using Protocol.Interfaces;

public interface ITransportService : IDisposable
{
    Task InitializeAsync();
    Task WriteMessageAsync(IMessage message, CancellationToken cancellationToken = default);

    event EventHandler<MemoryStream>? MessageReceived;
    event EventHandler<Exception>? ExceptionRaised;

    bool IsInitiator { get; }
    bool IsConnected { get; }
    CompactPubKey? RemoteStaticPublicKey { get; }

    /// <summary>The bytes written to the connection so far (handshake and encrypted frames).</summary>
    long BytesSent => 0;

    /// <summary>The bytes read from the connection so far (handshake and encrypted frames).</summary>
    long BytesReceived => 0;
}