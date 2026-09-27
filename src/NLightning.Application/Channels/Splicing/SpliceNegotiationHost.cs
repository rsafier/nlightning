namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using InteractiveTx.Interfaces;
using InteractiveTx.Models;

/// <summary>
/// The splice as the dependent protocol of an interactive-tx negotiation (splicing plan §3.5 steps 4-6, §3.9;
/// <see cref="InteractiveTxPurpose.Splice"/>): one instance per splice negotiation, handed to
/// <see cref="IInteractiveTxDriver.StartAsync"/>. The driver calls it under the channel's lock; it delegates to
/// <see cref="SpliceService"/>, which owns the negotiation.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="GetSharedFundingAsync"/>: the current funding output as the shared input (SP-TX-01) and the new
/// funding output (both new funding keys, the previous capacity with both contributions, SP-TX-03), with each side's
/// share.</item>
/// <item><see cref="CreateCommitmentSignedAsync"/>: the constructed transaction checked against SP-TX-01..05, then our
/// <c>commitment_signed</c> for the new funding at the current commitment number (SP-CS-01).</item>
/// <item><see cref="SignSharedInputAsync"/> / <see cref="BuildSharedInputWitness"/>: <c>shared_input_signature</c>
/// (SP-SIG-01, refused by the signer before SP-I1 holds) and the 2-of-2 witness.</item>
/// <item><see cref="OnCompletedAsync"/>: the splice transaction as a pending <c>BroadcastTransactions</c> row, its
/// confirmation watch and the pending funding, in the driver's save; quiescence then ends (SP-Q-01).</item>
/// <item><see cref="OnRbfRequestedAsync"/>: splice RBF is wave SPR, so it is rejected.</item>
/// </list>
/// </remarks>
public sealed class SpliceNegotiationHost : IInteractiveTxHost
{
    private readonly SpliceNegotiation _negotiation;
    private readonly SpliceService _service;

    internal SpliceNegotiationHost(SpliceService service, SpliceNegotiation negotiation)
    {
        _service = service;
        _negotiation = negotiation;
    }

    /// <inheritdoc />
    public InteractiveTxPurpose Purpose => InteractiveTxPurpose.Splice;

    /// <inheritdoc />
    public Task<SharedFundingSpec?> GetSharedFundingAsync(InteractiveTxTerms terms,
                                                          CancellationToken cancellationToken) =>
        Task.FromResult(_negotiation.SharedFunding);

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> CreateCommitmentSignedAsync(InteractiveTxSessionModel session,
                                                                            IUnitOfWork unitOfWork,
                                                                            CancellationToken cancellationToken) =>
        _service.CreateSpliceCommitmentSignedAsync(_negotiation, session, unitOfWork, cancellationToken);

    /// <inheritdoc />
    public Task<CompactSignature?> SignSharedInputAsync(ConstructedInteractiveTx transaction,
                                                        CancellationToken cancellationToken) =>
        Task.FromResult<CompactSignature?>(_service.SignSharedInput(_negotiation, transaction));

    /// <inheritdoc />
    public Witness BuildSharedInputWitness(ConstructedInteractiveTx transaction, CompactSignature localSignature,
                                           CompactSignature remoteSignature) =>
        _service.BuildSharedInputWitness(_negotiation, transaction, localSignature, remoteSignature);

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> OnCompletedAsync(InteractiveTxCompletion completion,
                                                                 IUnitOfWork unitOfWork,
                                                                 CancellationToken cancellationToken) =>
        _service.OnSpliceSignedAsync(_negotiation, completion, unitOfWork, cancellationToken);

    /// <inheritdoc />
    public Task OnAbortedAsync(ChannelId channelId, string reason, CancellationToken cancellationToken) =>
        _service.OnSpliceAbortedAsync(_negotiation, reason, cancellationToken);

    /// <inheritdoc />
    public Task<InteractiveTxRbfDecision> OnRbfRequestedAsync(TxInitRbfMessage message,
                                                              IReadOnlyList<ConstructedInteractiveTx> previousAttempts,
                                                              CancellationToken cancellationToken) =>
        Task.FromResult(InteractiveTxRbfDecision.Reject("splice rbf is not supported yet"));
}