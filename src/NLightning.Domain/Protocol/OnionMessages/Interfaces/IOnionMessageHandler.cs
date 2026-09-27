namespace NLightning.Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// Consumes onion messages delivered to us whose final hop carries one of <see cref="PayloadTypes"/> (BOLT 4: types
/// 64 and up are final-hop payload fields; BOLT 12 uses 64 <c>invoice_request</c>, 66 <c>invoice</c> and 68
/// <c>invoice_error</c>).
/// </summary>
/// <remarks>
/// Replies to <c>IOnionMessageService.SendAndWaitForReplyAsync</c> go to that caller, not to a handler. Handlers run off
/// the peer read loop, on a bounded queue; register them as singletons.
/// </remarks>
public interface IOnionMessageHandler
{
    /// <summary>
    /// The final-hop payload field types this handler consumes. No two handlers share a type.
    /// </summary>
    IReadOnlyCollection<ulong> PayloadTypes { get; }

    /// <summary>
    /// Handles one delivered message. To answer, send to <see cref="ReceivedOnionMessage.ReplyPath"/> through
    /// <see cref="IOnionMessageService"/>.
    /// </summary>
    /// <param name="message">The delivered message, with its reply path and our path_id.</param>
    /// <param name="cancellationToken">Stops the handling (node shutdown).</param>
    Task HandleAsync(ReceivedOnionMessage message, CancellationToken cancellationToken);
}