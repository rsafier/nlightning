using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Exceptions;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Domain.Signing.Recovery;
using Domain.Signing.Vls;
using Safety;
using Safety.Interfaces;

/// <summary>
/// VLS close signatures as node workflows (NL-1330): the intent is saved with the original request envelope before
/// VLS is called, the exact receipt is saved before it is used, the application consumes the workflow in the save of
/// its transition (the closing transaction with <c>Closing</c>, or the commitment's broadcast row with <c>Failed</c>),
/// and publication only follows that save. Recovery replays the saved request; an unknown outcome blocks.
/// </summary>
/// <remarks>Inactive (every method a no-op or refused) unless the node signs through VLS.</remarks>
public sealed class VlsCloseSigningWorkflows
{
    private readonly IRemoteSigningWorkflowCoordinator? _coordinator;
    private readonly IServiceProvider _services;
    private readonly IVlsChannelSigner? _vlsSigner;

    public VlsCloseSigningWorkflows(IServiceProvider services, IRemoteSigningWorkflowCoordinator? coordinator = null,
                                    IVlsChannelSigner? vlsSigner = null)
    {
        _services = services;
        _coordinator = coordinator;
        _vlsSigner = vlsSigner;
    }

    public bool Enabled => _vlsSigner is not null;

    private IRemoteSigningWorkflowCoordinator Coordinator =>
        _coordinator ?? throw new InvalidOperationException("VLS close signing requires durable signing workflows.");

    /// <summary>The intent of one mutual close signature (not activated: the caller activates, signs and consumes).</summary>
    public Task<ISigningWorkflowScope> BeginMutualCloseAsync(ChannelModel channel, ulong holderSatoshis,
                                                             ulong peerSatoshis, BitcoinScript? holderScript,
                                                             BitcoinScript? peerScript)
        => Coordinator.BeginAsync(Descriptor(channel, SigningWorkflowKind.MutualClose,
                                             VlsCloseFingerprint.MutualClose(holderSatoshis, peerSatoshis,
                                                 holderScript is { } h ? (byte[])h : null,
                                                 peerScript is { } p ? (byte[])p : null)));

    /// <summary>The intent of signing local commitment <paramref name="number"/> for broadcast (not activated).</summary>
    public Task<ISigningWorkflowScope> BeginForceCloseAsync(ChannelModel channel, ulong number, TxId unsignedTxId)
        => Coordinator.BeginAsync(Descriptor(channel, SigningWorkflowKind.ForceClose,
                                             VlsCloseFingerprint.ForceClose(number, unsignedTxId)));

    /// <summary>
    /// Our latest commitment as it was signed and saved for broadcast before (its exact bytes), so a retried or resumed
    /// failure publishes the saved transaction instead of asking VLS again; null when none was saved for it.
    /// </summary>
    public async Task<SignedLocalCommitment?> FindSavedForceCloseAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                                       LocalCommitmentBroadcastBuilder builder)
    {
        if (!Enabled)
            return null;

        var (number, unsignedTxId, htlcOutputs) = builder.Describe(channel);
        var rows = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId) ?? [];
        var saved = rows.FirstOrDefault(b => b.Purpose == BroadcastPurpose.LocalCommitment
                                          && b.CommitmentNumber == number && b.TransactionId == unsignedTxId);
        return saved is null ? null : new SignedLocalCommitment(number, saved.ToSignedTransaction(), htlcOutputs);
    }

    /// <summary>
    /// Startup (under the channel's lock): finishes an interrupted close signature with its original request. A mutual
    /// close signature that was never sent is replayed and retired (the negotiation restarts on the next connection);
    /// an interrupted force close is completed: the saved request is replayed, the commitment and <c>Failed</c> saved
    /// with the workflow consumed, then published.
    /// </summary>
    public async Task ResumeAsync(ChannelModel channel, SigningWorkflow pending, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(pending);
        if (!Enabled || pending.State != SigningWorkflowState.Pending)
            throw new InvalidOperationException("Only a pending VLS close workflow can be resumed.");

        switch (pending.Kind)
        {
            case SigningWorkflowKind.MutualClose:
                var recovery = Coordinator as IVlsCloseSigningRecovery
                            ?? throw new InvalidOperationException("VLS close recovery is unavailable.");
                using (var workflow = await recovery.ResumeSavedMutualCloseAsync(pending))
                {
                    workflow.Activate();
                    // The signature was saved before anything could send it: nothing to send, only the outcome to settle
                    recovery.ReplayCloseSignature(workflow);
                    await workflow.StageConsumeAsync(unitOfWork);
                    await unitOfWork.SaveChangesAsync();
                }

                break;
            case SigningWorkflowKind.ForceClose:
                var failures = _services.GetRequiredService<IChannelFailureService>();
                var prepared = await failures.PrepareFailureUnderLockAsync(
                                   channel.ChannelId,
                                   new ChannelFailureRequest("resuming an interrupted VLS force close",
                                                             ChannelFailedException.DefaultPeerMessage));
                if ((await Coordinator.GetPendingAsync(channel.ChannelId)).Count != 0)
                    throw new InvalidOperationException(
                        $"The interrupted VLS force close of channel {channel.ChannelId} could not be completed.");
                await failures.CompleteFailureAsync(prepared, sendError: false);
                break;
            default:
                throw new InvalidOperationException("Not a VLS close workflow.");
        }
    }

    private static SigningWorkflowDescriptor Descriptor(ChannelModel channel, SigningWorkflowKind kind,
                                                        byte[] fingerprint)
        => new(channel.ChannelId, kind, channel.Commitments?.LocalCommit.Number ?? channel.LocalCommitmentNumber,
               channel.Commitments?.RemoteCommit.Number ?? channel.RemoteCommitmentNumber, fingerprint);
}