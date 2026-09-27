namespace NLightning.Domain.Protocol.OnionMessages.Interfaces;

using Messages;
using Node.Interfaces;
using Onion.Models;

/// <summary>
/// The node's onion-message endpoint (BOLT 4 "Onion Messages", wave M6): receives, forwards and delivers incoming
/// <c>onion_message</c>s, and sends our own.
/// </summary>
/// <remarks>
/// <para>Receive: peel with an empty associated data and the message's path_key, unblind the
/// <c>encrypted_recipient_data</c>, then forward to the next node (only to a connected peer, never back to the sender)
/// or deliver to the <see cref="IOnionMessageHandler"/> registered for the final hop's payload field. Any reader
/// failure is ignored: onion messages have no error replies.</para>
/// <para>Send: build unblinded hops up to the destination or its introduction node, with
/// <c>next_path_key_override = first_path_key</c> on the last one (BOLT 4 writer), in a 1300- or 32768-byte packet.
/// </para>
/// </remarks>
public interface IOnionMessageService
{
    /// <summary>
    /// Whether onion messages are on (<c>option_onion_messages</c> advertised). When false, incoming messages are
    /// dropped and every send returns <see cref="Enums.OnionMessageSendStatus.NotAvailable"/>.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Takes an <c>onion_message</c> received from <paramref name="peer"/>. Called on the peer's read loop: it only
    /// queues (after the rate limit) and never blocks or throws for a bad message.
    /// </summary>
    /// <param name="peer">The connection it arrived on.</param>
    /// <param name="message">The message.</param>
    void HandleIncoming(IPeerService peer, OnionMessageMessage message);

    /// <summary>
    /// Sends one onion message. It is sent once: retrying over another path (BOLT 4 SHOULD) is the caller's choice.
    /// </summary>
    /// <param name="destination">A node id, or a blinded path (for example the <c>reply_path</c> of a message we
    /// answer).</param>
    /// <param name="contents">The final hop's records.</param>
    /// <param name="replyPath">A <c>reply_path</c> to include (BOLT 4: only when a reply is allowed), or null.</param>
    /// <param name="cancellationToken">Stops the send.</param>
    /// <returns><see cref="Enums.OnionMessageSendStatus.Sent"/>, or why it was not sent.</returns>
    Task<OnionMessageSendResult> SendAsync(OnionMessageDestination destination, OnionMessageContents contents,
                                           BlindedPath? replyPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one onion message with a fresh <c>reply_path</c> to us (its <c>path_id</c> is a secret of ours) and waits
    /// for a reply of one of <paramref name="expectedReplyTypes"/> through it. A message through that path with another
    /// type, or after the deadline, is ignored (BOLT 4 reader).
    /// </summary>
    /// <param name="destination">A node id or a blinded path.</param>
    /// <param name="contents">The final hop's records.</param>
    /// <param name="expectedReplyTypes">The payload field types a reply may carry (for example 66 and 68 after an
    /// <c>invoice_request</c>).</param>
    /// <param name="timeout">How long to wait for the reply.</param>
    /// <param name="cancellationToken">Stops the send and the wait.</param>
    /// <returns><see cref="Enums.OnionMessageSendStatus.Replied"/> with the reply,
    /// <see cref="Enums.OnionMessageSendStatus.ReplyTimedOut"/>, or why it was not sent.</returns>
    Task<OnionMessageSendResult> SendAndWaitForReplyAsync(OnionMessageDestination destination,
                                                          OnionMessageContents contents,
                                                          IReadOnlyCollection<ulong> expectedReplyTypes,
                                                          TimeSpan timeout,
                                                          CancellationToken cancellationToken = default);
}