namespace NLightning.Application.Tests.InteractiveTx.TestDoubles;

using Application.InteractiveTx.Interfaces;
using Application.InteractiveTx.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// The IT4-T3 test host: a dummy "shared funding" purpose where both sides pay into one shared output (and, when
/// <see cref="SharedInput"/> is set, spend a shared input as a splice would). It records every callback.
/// </summary>
internal sealed class TestSharedFundingHost : IInteractiveTxHost
{
    public static readonly BitcoinScript SharedOutputScript = new([0x00, 0x20, .. Enumerable.Repeat((byte)0xAB, 32)]);

    public required LightningMoney LocalOutputShare { get; set; }
    public required LightningMoney RemoteOutputShare { get; set; }
    public SharedFundingInput? SharedInput { get; init; }
    public LightningMoney LocalInputShare { get; init; } = LightningMoney.Zero;
    public LightningMoney RemoteInputShare { get; init; } = LightningMoney.Zero;
    public bool FailCommitmentStep { get; set; }
    public Func<TxInitRbfMessage, IReadOnlyList<ConstructedInteractiveTx>, InteractiveTxRbfDecision>? RbfHandler
    {
        get;
        set;
    }

    public List<InteractiveTxSessionModel> CommitmentSteps { get; } = [];
    public List<InteractiveTxCompletion> Completions { get; } = [];
    public List<string> Aborts { get; } = [];
    public List<TxInitRbfMessage> RbfRequests { get; } = [];

    public InteractiveTxPurpose Purpose => InteractiveTxPurpose.DualFund;

    public Task<SharedFundingSpec?> GetSharedFundingAsync(InteractiveTxTerms terms,
                                                          CancellationToken cancellationToken)
    {
        var total = LocalOutputShare + RemoteOutputShare;
        return Task.FromResult<SharedFundingSpec?>(new SharedFundingSpec(SharedInput, SharedOutputScript, total,
                                                                         LocalInputShare, RemoteInputShare,
                                                                         LocalOutputShare, RemoteOutputShare));
    }

    public Task<IReadOnlyList<IChannelMessage>> CreateCommitmentSignedAsync(InteractiveTxSessionModel session,
                                                                            IUnitOfWork unitOfWork,
                                                                            CancellationToken cancellationToken)
    {
        if (FailCommitmentStep)
            throw new InvalidOperationException("test: the commitment cannot be signed");

        CommitmentSteps.Add(session);
        return Task.FromResult<IReadOnlyList<IChannelMessage>>([new TestCommitmentSignedMessage(session.ChannelId)]);
    }

    public Task<CompactSignature?> SignSharedInputAsync(ConstructedInteractiveTx transaction,
                                                        CancellationToken cancellationToken) =>
        Task.FromResult<CompactSignature?>(new CompactSignature(Enumerable.Repeat((byte)0x30, 64).ToArray()));

    public Witness BuildSharedInputWitness(ConstructedInteractiveTx transaction, CompactSignature localSignature,
                                           CompactSignature remoteSignature) =>
        new([.. localSignature.Value, .. remoteSignature.Value]);

    public Task<IReadOnlyList<IChannelMessage>> OnCompletedAsync(InteractiveTxCompletion completion,
                                                                 IUnitOfWork unitOfWork,
                                                                 CancellationToken cancellationToken)
    {
        Completions.Add(completion);
        return Task.FromResult<IReadOnlyList<IChannelMessage>>([]);
    }

    public Task OnAbortedAsync(ChannelId channelId, string reason, CancellationToken cancellationToken)
    {
        Aborts.Add(reason);
        return Task.CompletedTask;
    }

    public Task<InteractiveTxRbfDecision> OnRbfRequestedAsync(TxInitRbfMessage message,
                                                              IReadOnlyList<ConstructedInteractiveTx> previousAttempts,
                                                              CancellationToken cancellationToken)
    {
        RbfRequests.Add(message);
        return Task.FromResult(RbfHandler?.Invoke(message, previousAttempts)
                            ?? InteractiveTxRbfDecision.Reject("test host does not rbf"));
    }
}