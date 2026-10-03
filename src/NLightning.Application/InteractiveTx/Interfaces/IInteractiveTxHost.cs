namespace NLightning.Application.InteractiveTx.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Models;

/// <summary>
/// The protocol an interactive-tx negotiation serves (splicing plan §3.9): <c>ISpliceNegotiationHost</c> (wave SP1),
/// <c>IDualFundOpenHost</c> (wave DF) or a test host. The <see cref="IInteractiveTxDriver"/> calls it under the
/// channel's lock for everything that is not the negotiation itself: the shared funding spec, the commitment step,
/// the shared input's signature, the completion and the abort.
/// </summary>
/// <remarks>
/// Every callback runs under the channel's lock (never take it again, never await a send). Writes are staged on the
/// given <see cref="IUnitOfWork"/>; the driver commits them in the same save as the negotiation's row, before the
/// messages go out (persist before send).
/// </remarks>
public interface IInteractiveTxHost
{
    /// <summary>The protocol served (stored with the negotiation).</summary>
    InteractiveTxPurpose Purpose { get; }

    /// <summary>
    /// The shared input and output of the negotiation described by <paramref name="terms"/> (at its feerate), or null
    /// when there is none. Called once per attempt (a new attempt of an RBF may change the shares).
    /// </summary>
    Task<SharedFundingSpec?> GetSharedFundingAsync(InteractiveTxTerms terms, CancellationToken cancellationToken);

    /// <summary>
    /// The commitment step: the negotiation is complete and the transaction constructed
    /// (<see cref="InteractiveTxSessionModel.ConstructedTx"/>). Stage whatever the new funding needs and return our
    /// <c>commitment_signed</c> for it (BOLT 2: sent after the second <c>tx_complete</c>). The driver stores
    /// <paramref name="session"/> in the same save, then sends. Throwing aborts the negotiation with <c>tx_abort</c>.
    /// </summary>
    Task<IReadOnlyList<IChannelMessage>> CreateCommitmentSignedAsync(InteractiveTxSessionModel session,
                                                                     IUnitOfWork unitOfWork,
                                                                     CancellationToken cancellationToken);

    /// <summary>
    /// Our signature of the shared input for <c>shared_input_signature</c> (a splice), or null when the transaction
    /// spends no shared input.
    /// </summary>
    Task<CompactSignature?> SignSharedInputAsync(ConstructedInteractiveTx transaction,
                                                 CancellationToken cancellationToken);

    /// <summary>
    /// Asked right before we sign our <c>tx_signatures</c> for <paramref name="transaction"/>: the reason it can never
    /// confirm (an RBF sibling of the same funding confirmed, NL-867; BOLT 2: "If the previous transaction confirms in
    /// the middle of an RBF attempt, the attempt MUST be abandoned"), which makes the driver abort it with
    /// <c>tx_abort</c> instead, or null to sign. A host without such knowledge keeps the default.
    /// </summary>
    Task<string?> GetTxSignaturesRefusalAsync(ConstructedInteractiveTx transaction,
                                              CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>The shared input's full witness from both signatures (the 2-of-2 funding script spend).</summary>
    Witness BuildSharedInputWitness(ConstructedInteractiveTx transaction, CompactSignature localSignature,
                                    CompactSignature remoteSignature);

    /// <summary>
    /// Both <c>tx_signatures</c> were exchanged: the transaction is fully signed. Stage its broadcast and the protocol's
    /// next state; the returned messages are sent after the save.
    /// </summary>
    Task<IReadOnlyList<IChannelMessage>> OnCompletedAsync(InteractiveTxCompletion completion, IUnitOfWork unitOfWork,
                                                          CancellationToken cancellationToken);

    /// <summary>
    /// The negotiation ended with <c>tx_abort</c> (sent or received) or a disconnection before our
    /// <c>tx_signatures</c>: forget it. Earlier completed attempts (RBF) stay valid.
    /// </summary>
    Task OnAbortedAsync(ChannelId channelId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// The peer sent <c>tx_init_rbf</c> for the completed attempt(s) of this host, at a feerate that satisfies
    /// IT-RBF-01. Accept with the new attempt's terms, or reject.
    /// </summary>
    Task<InteractiveTxRbfDecision> OnRbfRequestedAsync(TxInitRbfMessage message,
                                                       IReadOnlyList<ConstructedInteractiveTx> previousAttempts,
                                                       CancellationToken cancellationToken);

    /// <summary>
    /// The peer answered our <c>tx_init_rbf</c> with <paramref name="message"/>, before the new attempt is created (so
    /// before <see cref="GetSharedFundingAsync"/> is called for it). BOLT 2: the peer "MAY set
    /// <c>funding_output_contribution</c> to a different value" than in its earlier messages, so a host whose shared
    /// output depends on it takes the new value here (NL-521). Returns null to go on, or the reason of the
    /// <c>tx_abort</c> that ends the RBF (then <see cref="OnAbortedAsync"/> follows). A host that learned the peer's
    /// contribution before the driver (a splice reads <c>tx_ack_rbf</c> itself) keeps the default.
    /// </summary>
    Task<string?> OnRbfAcknowledgedAsync(TxAckRbfMessage message, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Our <c>tx_init_rbf</c> ended before any attempt of it existed (NL-527): the peer answered it with
    /// <c>tx_abort</c>, both sides sent <c>tx_init_rbf</c> at once, our attempt could not be built after the peer's
    /// <c>tx_ack_rbf</c>, or the connection dropped. Whoever waits for the RBF learns why; the completed attempts stay
    /// valid. A host that follows the end of the RBF otherwise (a splice ends with its quiescence, which the driver
    /// terminates on <c>tx_abort</c>) keeps the default.
    /// </summary>
    Task OnRbfRequestEndedAsync(ChannelId channelId, string reason, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}