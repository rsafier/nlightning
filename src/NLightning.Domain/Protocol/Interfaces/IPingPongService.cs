namespace NLightning.Domain.Protocol.Interfaces;

/// <summary>
/// Interface for a ping pong service.
/// </summary>
public interface IPingPongService
{
    /// <summary>
    /// Starts the ping service.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task StartPingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a ping now (or joins the one already in flight) and waits for its pong (BOLT 2: ping before
    /// <c>commitment_signed</c> when the peer has been quiet).
    /// </summary>
    /// <param name="timeout">How long to wait for the pong.</param>
    /// <param name="cancellationToken">Stops waiting (no disconnect).</param>
    /// <returns>True when the pong arrived in time. False on a timeout, which also raises
    /// <see cref="DisconnectEvent"/> (BOLT 1: MAY close the connection).</returns>
    Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a pong message.
    /// </summary>
    /// <param name="message">The pong message.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    void HandlePong(IMessage message);

    /// <summary>
    /// Event that is raised when a ping message is ready to be sent.
    /// </summary>
    event EventHandler<IMessage> OnPingMessageReady;

    /// <summary>
    /// Event that is raised when a pong message is received.
    /// </summary>
    event EventHandler OnPongReceived;

    /// <summary>
    /// Event that is raised when the pong is not received in time or the pong message is invalid.
    /// </summary>
    event EventHandler<Exception>? DisconnectEvent;
}