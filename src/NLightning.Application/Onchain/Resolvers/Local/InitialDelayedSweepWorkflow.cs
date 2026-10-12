using System.Security.Cryptography;

namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Signing.Recovery;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>Captures a delayed sweep before signing and commits its receipt with the executor's output decision.</summary>
internal sealed class InitialDelayedSweepWorkflow(IRemoteSigningWorkflowCoordinator coordinator,
    INativeInitialSweepSigningRecovery recovery, ISweepTransactionBuilder builder, ILightningSigner signer)
{
    public async Task<IReadOnlyList<OutputResolverAction>?> ResumeAsync(ChannelCloseModel close,
        IReadOnlyList<OutputResolutionModel> outputs, uint height, IUnitOfWork unitOfWork)
    {
        var pending = await coordinator.GetPendingAsync(close.ChannelId);
        if (pending.Count == 0)
            return null;
        if (pending.Count != 1 || pending[0].State != SigningWorkflowState.Pending)
            throw new InvalidOperationException("Uncertain signing workflow blocks initial delayed sweeps.");
        var saved = pending[0];
        if (saved.Kind != SigningWorkflowKind.OnchainInitialSweep)
            return []; // The workflow's owner resumes it before a new resolver signing lifecycle starts.
        var encoded = saved.PublicationIntent
            ?? throw new InvalidOperationException("Initial delayed sweep lost its publication intent.");
        var intent = recovery.DecodeInitialSweepIntent(encoded);
        if (intent.Close.ChannelId != close.ChannelId || intent.Close.Kind != close.Kind
         || intent.Close.CommitmentTransactionId != close.CommitmentTransactionId
         || intent.Close.CommitmentNumber != close.CommitmentNumber)
            throw new InvalidOperationException("Initial delayed sweep belongs to another close snapshot.");
        var descriptor = Descriptor(intent, encoded);
        if (intent.Close.SpentAtHeight != close.SpentAtHeight || intent.Close.BlockHash != close.BlockHash)
            return Retirement(saved.WorkflowId, descriptor);
        var original = intent.Output;
        var current = outputs.SingleOrDefault(row => row.TransactionId == original.TransactionId
                                                  && row.OutputIndex == original.OutputIndex);
        if (current is null && intent.OutputWasPersisted)
            throw new InvalidOperationException("Initial delayed sweep lost its persisted output row.");
        current ??= original; // The initial output row and its signing receipt may both await the executor's first save.
        if (current.ChannelId != original.ChannelId || current.Descriptor != original.Descriptor
         || current.HtlcId != original.HtlcId || current.HtlcDirection != original.HtlcDirection
         || !current.DescriptorData.AsSpan().SequenceEqual(original.DescriptorData))
            throw new InvalidOperationException("Initial delayed sweep output prerequisites changed.");
        if (intent.Parent is { } parent)
        {
            var parentRow = outputs.SingleOrDefault(row => row.TransactionId == parent.Output.TransactionId
                                                        && row.OutputIndex == parent.Output.OutputIndex);
            if (parentRow is null || parentRow.ChannelId != parent.Output.ChannelId
             || parentRow.Descriptor != parent.Output.Descriptor || parentRow.HtlcId != parent.Output.HtlcId
             || parentRow.HtlcDirection != parent.Output.HtlcDirection
             || !parentRow.DescriptorData.AsSpan().SequenceEqual(parent.Output.DescriptorData)
             || parentRow.ResolvingTransactionId != original.TransactionId)
                throw new InvalidOperationException("Second-level sweep lost its original HTLC parent output.");
            var parentBroadcast = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(original.TransactionId);
            if (parentBroadcast is null || parentBroadcast.Purpose != BroadcastPurpose.HtlcTransaction
             || parentBroadcast.ChannelId != close.ChannelId)
                throw new InvalidOperationException("Second-level sweep parent transaction changed.");
            var parentWatch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(parent.Output.TransactionId, parent.Output.OutputIndex);
            if (parentWatch is null || parentWatch.SpentByTransactionId != original.TransactionId || parentWatch.SpentAtHeight != parent.ConfirmedHeight
             || (parentWatch.SpentBlockHash ?? Domain.Crypto.ValueObjects.Hash.Empty) != parent.ConfirmedBlockHash)
                return Retirement(saved.WorkflowId, descriptor);
        }
        var watch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(original.TransactionId, original.OutputIndex);
        if (current.State is OutputResolutionState.Resolved or OutputResolutionState.Irrevocable
         || watch?.SpentByTransactionId is not null)
            return Retirement(saved.WorkflowId, descriptor);
        if (current.ResolvingTransactionId is not null
         || current.State is not (OutputResolutionState.Pending or OutputResolutionState.Waiting))
            throw new InvalidOperationException("Initial delayed sweep no longer owns its output decision.");
        if (height < intent.Height)
            return [new SigningWorkflowRoundAction(saved.WorkflowId)];
        return await SignAsync(intent, encoded);
    }

    public Task<IReadOnlyList<OutputResolverAction>> StartAsync(InitialDelayedSweepIntent intent)
        => SignAsync(intent, recovery.EncodeInitialSweepIntent(intent));

    private async Task<IReadOnlyList<OutputResolverAction>> SignAsync(InitialDelayedSweepIntent intent, byte[] encoded)
    {
        var descriptor = Descriptor(intent, encoded);
        SignedTransaction signed;
        Guid workflowId;
        using (var workflow = await coordinator.BeginAsync(descriptor))
        {
            workflowId = workflow.WorkflowId;
            workflow.Activate();
            signed = builder.AddWitnesses(intent.Transaction, recovery.SignSweepInputs(workflow, signer));
            if (signed.TxId != intent.Transaction.Transaction.TxId)
                throw new InvalidOperationException("Signing changed the initial delayed sweep's transaction identity.");
        }
        var row = intent.Output with
        {
            State = OutputResolutionState.Broadcast,
            ResolvingTransactionId = signed.TxId,
            WaitUntilHeight = null
        };
        return [new SigningWorkflowRoundAction(workflowId), new UpsertOutputAction(row),
            new WatchOutpointAction(new WatchedOutpointModel(row.TransactionId, row.OutputIndex, row.ChannelId,
                WatchedOutpointPurpose.ResolutionOutput)),
            new BroadcastAction(new BroadcastTransactionModel(signed, BroadcastPurpose.Sweep, intent.Close.ChannelId,
                intent.Height, intent.FeeratePerKw, fee: Domain.Money.LightningMoney.Satoshis(intent.Transaction.FeeSat))),
            new StageWriteAction("consume initial delayed sweep signing receipt", async (uow, _) =>
            {
                using var workflow = await coordinator.BeginAsync(descriptor);
                if (workflow.WorkflowId != workflowId)
                    throw new InvalidOperationException("Initial delayed sweep lost its captured workflow identity.");
                workflow.Activate();
                var replayed = builder.AddWitnesses(intent.Transaction, recovery.SignSweepInputs(workflow, signer));
                if (replayed.TxId != signed.TxId || !replayed.RawTxBytes.AsSpan().SequenceEqual(signed.RawTxBytes))
                    throw new InvalidOperationException("Initial delayed sweep receipt changed before publication.");
                await workflow.StageConsumeAsync(uow);
            })];
    }

    private IReadOnlyList<OutputResolverAction> Retirement(Guid workflowId, SigningWorkflowDescriptor descriptor)
        => [new SigningWorkflowRoundAction(workflowId),
            new StageWriteAction("retire obsolete initial delayed sweep intent", (uow, _) =>
                recovery.StageRetireSweepAsync(descriptor, uow))];

    private static SigningWorkflowDescriptor Descriptor(InitialDelayedSweepIntent intent, byte[] encoded)
        => new(intent.Close.ChannelId, SigningWorkflowKind.OnchainInitialSweep, 0, 0, SHA256.HashData(encoded))
        { PublicationIntent = encoded };
}