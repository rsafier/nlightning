namespace NLightning.Application.InteractiveTx.Interfaces;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.Interfaces;
using Models;

/// <summary>
/// Runs the interactive-tx negotiations of every channel (BOLT 2 "Interactive Transaction Construction"; splicing plan
/// §3.9, IT4-T1): one negotiation per channel at a time, plus its completed attempts for RBF. Every member must be
/// called under the channel's lock (<c>IChannelLockProvider</c>); the returned messages go to the peer in order, after
/// the call (the staged writes are already saved).
/// </summary>
/// <remarks>
/// <para>A rule broken by the peer, or a message the negotiation cannot use, ends the negotiation with our
/// <c>tx_abort</c>, never with a channel failure. <c>tx_abort</c> is never sent after our <c>tx_signatures</c>
/// (IT-ABT-01); such a negotiation is kept until an input of its transaction is spent.</para>
/// <para>The negotiation is stored from the moment our <c>commitment_signed</c> for it is sent (in the save that
/// precedes sending it); an unsigned negotiation lives in memory only and is lost on disconnection.</para>
/// </remarks>
public interface IInteractiveTxDriver
{
    /// <summary>
    /// Starts a negotiation for <paramref name="host"/> on <see cref="InteractiveTxTerms.ChannelId"/>. As initiator the
    /// result holds our first message; otherwise it is empty and the peer's first <c>tx_add_*</c> is awaited.
    /// </summary>
    /// <exception cref="InvalidOperationException">A negotiation is already in progress on the channel.</exception>
    Task<IReadOnlyList<IChannelMessage>> StartAsync(InteractiveTxTerms terms, IInteractiveTxHost host,
                                                    CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a message of the peer: <c>tx_add_input</c>, <c>tx_add_output</c>, <c>tx_remove_input</c>,
    /// <c>tx_remove_output</c>, <c>tx_complete</c>, <c>tx_signatures</c>, <c>tx_init_rbf</c>, <c>tx_ack_rbf</c> or
    /// <c>tx_abort</c> (types 66-74). Without a negotiation in progress, 66-73 are answered with <c>tx_abort</c>
    /// and <c>tx_abort</c> is echoed unless it echoes ours.
    /// </summary>
    Task<IReadOnlyList<IChannelMessage>> ReceiveAsync(IChannelMessage message, CompactPubKey peerPubKey,
                                                      IUnitOfWork unitOfWork,
                                                      CancellationToken cancellationToken = default);

    /// <summary>
    /// The host verified the peer's <c>commitment_signed</c> for the negotiated funding (IT-SIG-03): our
    /// <c>tx_signatures</c> follow when we send first (IT-SIG-01), or when the peer's already arrived.
    /// </summary>
    /// <exception cref="InvalidOperationException">No constructed negotiation waits for a commitment_signed.</exception>
    Task<IReadOnlyList<IChannelMessage>> OnCommitmentSignedReceivedAsync(ChannelId channelId, IUnitOfWork unitOfWork,
                                                                         CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests an RBF of the channel's completed negotiation with <c>tx_init_rbf</c> (IT-RBF-01). The new attempt
    /// starts (as initiator) when the peer's <c>tx_ack_rbf</c> arrives.
    /// </summary>
    /// <param name="terms">The new attempt's terms (<see cref="InteractiveTxTerms.IsInitiator"/> true; its
    /// contribution must re-add an input of every previous attempt we contributed to).</param>
    /// <param name="fundingOutputContribution">Our <c>funding_output_contribution</c>, zero for none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="InvalidOperationException">No completed attempt, one in progress, or a feerate below the
    /// IT-RBF-01 minimum.</exception>
    Task<IReadOnlyList<IChannelMessage>> RequestRbfAsync(InteractiveTxTerms terms,
                                                         LightningMoney fundingOutputContribution,
                                                         CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts the channel's negotiation ourselves with <c>tx_abort</c>. Empty when nothing is in progress.
    /// </summary>
    /// <exception cref="InvalidOperationException">Our <c>tx_signatures</c> was already sent (IT-ABT-01).</exception>
    Task<IReadOnlyList<IChannelMessage>> AbortAsync(ChannelId channelId, string reason, IUnitOfWork unitOfWork,
                                                    CancellationToken cancellationToken = default);

    /// <summary>
    /// The channel's peer disconnected: a negotiation that is not stored yet is forgotten (its reservation released,
    /// its host told); a stored one stays for the reconnection (<c>next_funding</c>).
    /// </summary>
    Task OnDisconnectedAsync(ChannelId channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a stored negotiation (after a restart, or on <c>channel_reestablish</c> with <c>next_funding</c>) so the
    /// signature exchange can finish.
    /// </summary>
    /// <exception cref="InvalidOperationException">A negotiation is already in progress on the channel.</exception>
    Task ResumeAsync(InteractiveTxSessionModel model, InteractiveTxTerms terms, IInteractiveTxHost host,
                     CancellationToken cancellationToken = default);

    /// <summary>Whether an attempt is in progress on the channel (not aborted, not fully signed).</summary>
    bool IsNegotiating(ChannelId channelId);

    /// <summary>A view of the channel's state, or null when the driver holds nothing for it.</summary>
    InteractiveTxNegotiationInfo? GetInfo(ChannelId channelId);
}