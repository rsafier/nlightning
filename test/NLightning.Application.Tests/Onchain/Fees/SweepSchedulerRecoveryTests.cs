using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Fees;

using Application.Onchain.Fees;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Signing.Recovery;
using Resolvers.Local;
using static Resolvers.Local.LocalCommitResolutionHarness;

public sealed class SweepSchedulerRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Given_ReplyLost_When_RoundRestartsWithHigherFee_Then_SavedReplacementIsConsumed(bool taproot, bool inputResolved, bool originalConfirmed = false)
    {
        // Arrange: the signer completes the replacement, but the first round receives no reply.
        using var harness = new LocalCommitResolutionHarness(simpleTaproot: taproot);
        await harness.ResolveAsync();
        await harness.MineToAsync(CloseHeight + Csv - 1);
        var original = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep));
        harness.HoldMempool = true;
        await harness.MineToAsync(harness.Height + 6);
        var fees = new Mock<IFeeService>();
        fees.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Money.LightningMoney.Satoshis(1_000));
        var workflows = new Mock<IRemoteSigningWorkflowCoordinator>();
        var recovery = workflows.As<INativeSweepSigningRecovery>();
        SigningWorkflowDescriptor? saved = null;
        IReadOnlyList<SweepSigningContext>? contexts = null;
        IReadOnlyList<CompactSignature>? receipts = null;
        var consumed = false;
        IUnitOfWork? consumingUnitOfWork = null;
        var scope = new Mock<ISigningWorkflowScope>();
        scope.Setup(s => s.StageConsumeAsync(It.IsAny<IUnitOfWork>())).Returns((IUnitOfWork uow) =>
        {
            consumingUnitOfWork = uow;
            consumed = true;
            return Task.CompletedTask;
        });
        workflows.Setup(w => w.GetPendingAsync(It.IsAny<ChannelId>())).ReturnsAsync(() =>
            saved is null ? [] : [new SigningWorkflow(Guid.NewGuid(), saved.ChannelId, saved.Kind, 0, 0,
                saved.SnapshotFingerprint, [], "regtest", 1, SigningWorkflowState.Pending, 0, 0)
                { PublicationIntent = saved.PublicationIntent }]);
        workflows.Setup(w => w.BeginAsync(It.IsAny<SigningWorkflowDescriptor>()))
            .ReturnsAsync((SigningWorkflowDescriptor descriptor) =>
            {
                if (saved is not null)
                    Assert.Equal(saved.PublicationIntent, descriptor.PublicationIntent);
                saved = descriptor;
                return scope.Object;
            });
        recovery.Setup(r => r.EncodeSweepContexts(It.IsAny<IReadOnlyList<SweepSigningContext>>()))
            .Returns((IReadOnlyList<SweepSigningContext> captured) => { contexts = captured; return [1]; });
        recovery.Setup(r => r.StageRetireSweepAsync(It.IsAny<SigningWorkflowDescriptor>(), It.IsAny<IUnitOfWork>()))
            .Returns((SigningWorkflowDescriptor descriptor, IUnitOfWork _) =>
            {
                Assert.Equal(saved!.PublicationIntent, descriptor.PublicationIntent);
                saved = null;
                return Task.CompletedTask;
            });
        var replyLost = true;
        recovery.Setup(r => r.SignSweepInputs(It.IsAny<ISigningWorkflowScope>(), It.IsAny<ILightningSigner>()))
            .Returns((ISigningWorkflowScope _, ILightningSigner signer) =>
            {
                Assert.NotNull(saved); // The intent is persisted before the first signing request.
                receipts ??= contexts!.Select(context => signer.SignSweepInput(saved.ChannelId, context)).ToArray();
                if (replyLost)
                    throw new IOException("Signer completed before the reply was delivered.");
                return receipts;
            });
        SweepScheduler CreateScheduler() => new(fees.Object, harness.GetService<ILightningSigner>(),
            NullLogger<SweepScheduler>.Instance, new SweepFeePolicy(new SweepFeePolicyOptions { SweepConfTarget = 6 }), signingWorkflows: workflows.Object);
        var uow = harness.CreateUnitOfWorkForTests();
        await Assert.ThrowsAsync<IOException>(() => CreateScheduler().PlanAsync(harness.Close,
            harness.Rows.Values.ToList(), harness.Height, uow, TestContext.Current.CancellationToken));
        Assert.False(consumed);
        var unsigned = contexts![0].UnsignedTransaction.ToArray();
        fees.Invocations.Clear();
        fees.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Money.LightningMoney.Satoshis(50_000));
        replyLost = false;

        if (inputResolved)
        {
            // A competing confirmed spend fences the saved intent before another signer call.
            var input = original.Inputs[0].PrevOut;
            var outputs = harness.Rows.Values.Select(row =>
                row.TransactionId == new Domain.Bitcoin.ValueObjects.TxId(input.Hash.ToBytes()) && row.OutputIndex == input.N
                    ? row with { State = originalConfirmed ? row.State : OutputResolutionState.Resolved }
                    : row).ToList();
            if (originalConfirmed)
                harness.Broadcasts[new Domain.Bitcoin.ValueObjects.TxId(original.GetHash().ToBytes())]
                    .MarkConfirmed(harness.Height, new Domain.Crypto.ValueObjects.Hash(new byte[32]));
            recovery.Invocations.Clear();
            var retirement = await CreateScheduler().PlanAsync(harness.Close, outputs,
                harness.Height + 1, uow, TestContext.Current.CancellationToken);
            Assert.Empty(retirement.OfType<BroadcastAction>());
            Assert.False(consumed);
            Assert.Empty(fees.Invocations);
            recovery.Verify(r => r.SignSweepInputs(It.IsAny<ISigningWorkflowScope>(), It.IsAny<ILightningSigner>()), Times.Never);
            recovery.Verify(r => r.StageRetireSweepAsync(It.IsAny<SigningWorkflowDescriptor>(), It.IsAny<IUnitOfWork>()), Times.Never);
            await Assert.Single(retirement.OfType<StageWriteAction>()).Stage(uow, TestContext.Current.CancellationToken);
            Assert.Null(saved);

            // An independent remaining claim progresses in the next round after retirement.
            var oldRow = outputs.Single(row => row.TransactionId == new Domain.Bitcoin.ValueObjects.TxId(input.Hash.ToBytes())
                                           && row.OutputIndex == input.N);
            var otherInputTxId = new Domain.Bitcoin.ValueObjects.TxId(Enumerable.Repeat((byte)42, 32).ToArray());
            var other = original.Clone();
            other.Inputs[0].PrevOut = new NBitcoin.OutPoint(new NBitcoin.uint256((byte[])otherInputTxId), input.N);
            var otherBroadcast = new BroadcastTransactionModel(new Domain.Bitcoin.ValueObjects.SignedTransaction(
                new Domain.Bitcoin.ValueObjects.TxId(other.GetHash().ToBytes()), other.ToBytes()), BroadcastPurpose.HtlcClaim,
                harness.Close.ChannelId, harness.Height - 6, 1_000);
            harness.Broadcasts[otherBroadcast.TransactionId] = otherBroadcast;
            outputs.Add(oldRow with
            {
                TransactionId = otherInputTxId,
                State = OutputResolutionState.Broadcast,
                ResolvingTransactionId = otherBroadcast.TransactionId
            });
            receipts = null;
            var next = await CreateScheduler().PlanAsync(harness.Close, outputs,
                harness.Height + 1, uow, TestContext.Current.CancellationToken);
            var claim = Assert.Single(next.OfType<BroadcastAction>()).Transaction;
            Assert.Equal(otherBroadcast.TransactionId, claim.ReplacesTransactionId);
            Assert.Equal(BroadcastPurpose.HtlcClaim, claim.Purpose);
            Assert.True(consumed);
            return;
        }

        // Act: a new scheduler resumes the saved intent before consulting a newer fee estimate.
        var actions = await CreateScheduler().PlanAsync(harness.Close, harness.Rows.Values.ToList(),
            harness.Height + 1, uow, TestContext.Current.CancellationToken);

        // Assert: the exact replacement is staged with consumption in the executor's unit of work.
        var replacement = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.True(consumed);
        Assert.Same(uow, consumingUnitOfWork);
        Assert.Empty(fees.Invocations);
        Assert.Equal(unsigned, contexts[0].UnsignedTransaction);
        Assert.Equal(new Domain.Bitcoin.ValueObjects.TxId(original.GetHash().ToBytes()), replacement.ReplacesTransactionId);
        Assert.Equal(harness.Height, replacement.FirstBroadcastHeight);
        Assert.NotEmpty(actions.OfType<UpsertOutputAction>());
        Assert.Single(actions.OfType<StageWriteAction>());
        harness.AssertAllInputsVerify(NBitcoin.Transaction.Load(replacement.RawTransaction, NBitcoin.Network.Main));
    }
}