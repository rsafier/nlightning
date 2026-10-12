namespace NLightning.Application.Channels.Splicing.Interfaces;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Takes the peer's <c>commitment_signed</c> for a splice's new funding (BOLT 2 splicing, SP-CS-01/02): the one sent
/// after the second <c>tx_complete</c>, which is not a normal commitment update (same commitment number, answered by
/// no <c>revoke_and_ack</c>). <c>ChannelManager</c> asks <see cref="IsSpliceCommitmentSigned"/> before the normal
/// <c>commitment_signed</c> handler, under the channel's lock.
/// </summary>
public interface ISpliceCommitmentReceiver
{
    /// <summary>
    /// Whether <paramref name="message"/> is the peer's <c>commitment_signed</c> for the splice negotiated on
    /// <paramref name="channelId"/>: the splice transaction is constructed, our own <c>commitment_signed</c> for it was
    /// sent, the peer's was not received yet, and <c>funding_txid</c> names the splice transaction (or is absent).
    /// </summary>
    bool IsSpliceCommitmentSigned(ChannelId channelId, CommitmentSignedMessage message);

    /// <summary>
    /// Under the lock: verifies and persists the peer's splice <c>commitment_signed</c> (SP-CS-02, SP-I7), then hands it
    /// to the interactive-tx driver, whose <c>tx_signatures</c> may follow (IT-SIG-01/03). A <c>commitment_signed</c>
    /// that does not verify ends the negotiation with our <c>tx_abort</c>.
    /// </summary>
    /// <returns>The messages to send, in order.</returns>
    Task<IReadOnlyList<IChannelMessage>> HandleSpliceCommitmentSignedAsync(CommitmentSignedMessage message,
                                                                           CompactPubKey peerPubKey,
                                                                           IUnitOfWork unitOfWork,
                                                                           CancellationToken cancellationToken =
                                                                               default);
}