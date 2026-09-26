namespace NLightning.Domain.Node.Interfaces;

using Crypto.ValueObjects;
using Exceptions;
using Protocol.Interfaces;

/// <summary>
/// Interface for communication with a single peer.
/// </summary>
public interface IPeerCommunicationService : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the connection is established.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the peer's public key.
    /// </summary>
    CompactPubKey PeerCompactPubKey { get; }

    /// <summary>
    /// When the last message of any type was received from the peer (UTC), or null before the first one.
    /// </summary>
    DateTimeOffset? LastMessageReceivedAt { get; }

    /// <summary>
    /// Sends a <c>ping</c> (or joins the one in flight) and waits for its <c>pong</c>. A timeout closes the
    /// connection (BOLT 1 MAY).
    /// </summary>
    /// <param name="timeout">How long to wait for the pong.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>True when the pong arrived in time; false on a timeout or before the init exchange finished.</returns>
    Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when a message is received from the peer.
    /// </summary>
    event EventHandler<IMessage?> MessageReceived;

    /// <summary>
    /// Event raised when the peer is disconnected.
    /// </summary>
    event EventHandler<Exception?>? DisconnectEvent;

    /// <summary>
    /// Event raised when an exception occurs.
    /// </summary>
    event EventHandler<Exception>? ExceptionRaised;

    /// <summary>
    /// Sends a message to the peer.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SendMessageAsync(IMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a warning message to the peer.
    /// </summary>
    /// <param name="we">The warning exception to send.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SendWarningAsync(WarningException we, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the communication with the peer.
    /// </summary>
    /// <param name="networkTimeout">The network timeout.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task InitializeAsync(TimeSpan networkTimeout);

    /// <summary>
    /// Disconnects from the peer.
    /// </summary>
    /// <param name="exception">The exception that caused the disconnection, if any.</param>
    void Disconnect(Exception? exception = null);
}