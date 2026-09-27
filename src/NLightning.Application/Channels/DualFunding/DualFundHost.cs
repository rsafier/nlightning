namespace NLightning.Application.Channels.DualFunding;

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
/// The dual-funded open as an interactive-tx host (splicing plan §3.9, wave DF): no shared input, the channel's
/// funding output as the shared output (added by the session's initiator), the zero-HTLC first commitment as the
/// commitment step and the funding broadcast at completion. Purpose <see cref="InteractiveTxPurpose.DualFund"/> for the
/// first attempt, <see cref="InteractiveTxPurpose.DualFundRbf"/> once an attempt is fully signed. One host per channel,
/// kept for its RBF attempts (the driver keeps completed attempts only while the host stays the same).
/// </summary>
internal sealed class DualFundHost : IInteractiveTxHost
{
    private readonly DualFundedOpenService _service;
    private readonly DualFundNegotiation _negotiation;

    public DualFundHost(DualFundedOpenService service, DualFundNegotiation negotiation)
    {
        _service = service;
        _negotiation = negotiation;
    }

    /// <inheritdoc />
    public InteractiveTxPurpose Purpose =>
        _negotiation.CompletedTxIds.Count > 0 ? InteractiveTxPurpose.DualFundRbf : InteractiveTxPurpose.DualFund;

    /// <inheritdoc />
    public Task<SharedFundingSpec?> GetSharedFundingAsync(InteractiveTxTerms terms,
                                                         CancellationToken cancellationToken) =>
        Task.FromResult<SharedFundingSpec?>(_service.GetSharedFunding(_negotiation));

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> CreateCommitmentSignedAsync(InteractiveTxSessionModel session,
                                                                            IUnitOfWork unitOfWork,
                                                                            CancellationToken cancellationToken) =>
        _service.CreateCommitmentSignedAsync(_negotiation, session, unitOfWork);

    /// <inheritdoc />
    public Task<CompactSignature?> SignSharedInputAsync(ConstructedInteractiveTx transaction,
                                                        CancellationToken cancellationToken) =>
        Task.FromResult<CompactSignature?>(null);

    /// <inheritdoc />
    public Witness BuildSharedInputWitness(ConstructedInteractiveTx transaction, CompactSignature localSignature,
                                           CompactSignature remoteSignature) =>
        throw new InvalidOperationException("A dual-funded open has no shared input");

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> OnCompletedAsync(InteractiveTxCompletion completion,
                                                                 IUnitOfWork unitOfWork,
                                                                 CancellationToken cancellationToken) =>
        _service.OnCompletedAsync(_negotiation, completion, unitOfWork);

    /// <inheritdoc />
    public Task OnAbortedAsync(ChannelId channelId, string reason, CancellationToken cancellationToken) =>
        _service.OnAbortedAsync(_negotiation, reason);

    /// <inheritdoc />
    public Task<InteractiveTxRbfDecision> OnRbfRequestedAsync(TxInitRbfMessage message,
                                                              IReadOnlyList<ConstructedInteractiveTx> previousAttempts,
                                                              CancellationToken cancellationToken) =>
        Task.FromResult(_service.DecideRbf(_negotiation, message));
}