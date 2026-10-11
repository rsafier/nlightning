using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Onchain.Resolvers.Local;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Signing.Recovery;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using static LocalCommitResolutionHarness;

public sealed class InitialDelayedSweepWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_LostFirstChildRowAndWatch_When_ActualResolverRestarts_Then_SecondLevelSweepIsCapturedAndItsSpendResolves(bool taproot)
    {
        // Arrange: the actual HTLC-success is confirmed and its delayed output is mature.
        using var harness = await CreateMatureOutputAsync(taproot, true);
        var child = FindOutput(harness, true);
        var toLocal = FindOutput(harness, false);
        harness.Rows[(toLocal.TransactionId, toLocal.OutputIndex)] = toLocal with
        { State = OutputResolutionState.Broadcast, ResolvingTransactionId = new Domain.Bitcoin.ValueObjects.TxId(Enumerable.Repeat((byte)8, 32).ToArray()) };
        harness.Rows.Remove((child.TransactionId, child.OutputIndex));
        harness.Watches.Remove((child.TransactionId, child.OutputIndex));
        var fake = new Capture(harness.GetService<ILightningSigner>()) { LoseReply = true };
        Application.Onchain.Resolvers.LocalCommitResolver Resolver() => new(
            harness.GetService<ICommitmentOutputMapper>(), harness.GetService<IHtlcTransactionBuilder>(),
            harness.GetService<ISweepTransactionBuilder>(), fake.Signer, harness.GetService<IFeeService>(),
            harness.GetService<ISweepDestinationProvider>(), harness.GetService<IServiceScopeFactory>(),
            NullLogger<Application.Onchain.Resolvers.LocalCommitResolver>.Instance,
            chainService: harness.GetService<Infrastructure.Bitcoin.Wallet.Interfaces.IBitcoinChainService>(),
            signingWorkflows: fake.Coordinator.Object);
        await Assert.ThrowsAsync<IOException>(() => Resolver().ResolveAsync(harness.Close, harness.Rows.Values.ToList(),
            harness.Height, TestContext.Current.CancellationToken));
        Assert.NotNull(fake.Intent);
        Assert.False(fake.Intent.OutputWasPersisted);
        Assert.Equal(child.TransactionId, fake.Intent.Transaction.Inputs[0].TxId);
        Assert.Equal(OutputDescriptorKind.DelayedToLocal, fake.Intent.Output.Descriptor);
        Assert.NotNull(fake.Intent.Parent);
        Assert.Equal(child.TransactionId, fake.Intent.Parent.Output.ResolvingTransactionId);
        Assert.Equal(OutputResolutionState.Irrevocable, fake.Intent.Parent.Output.State);
        Assert.Equal(fake.Intent.Parent.ConfirmedHeight, fake.Intent.Parent.Output.ResolvedHeight);
        Assert.True(harness.Height - fake.Intent.Parent.ConfirmedHeight + 1 >= 100);
        Assert.False(harness.Watches.ContainsKey((child.TransactionId, child.OutputIndex)));
        fake.LoseReply = false;

        // Act: replay the actual resolver call; both the first child row and its lost watch are restored.
        var actions = await Resolver().ResolveAsync(harness.Close, harness.Rows.Values.ToList(), harness.Height,
            TestContext.Current.CancellationToken);
        var broadcast = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        var watch = Assert.Single(actions.OfType<WatchOutpointAction>()).Watch;
        Assert.Equal(child.TransactionId, watch.TransactionId);
        Assert.Equal(child.OutputIndex, watch.OutputIndex);
        harness.AssertAllInputsVerify(Transaction.Load(broadcast.RawTransaction, Network.Main));
        await harness.ApplyActionsAsync(actions);
        Assert.True(fake.Consumed);
        Assert.Equal(1, fake.SignExecutions);
        Assert.True(harness.Watches.ContainsKey((child.TransactionId, child.OutputIndex)));

        // Assert: the persisted watch observes the confirming sweep and resolves that precise child output.
        await harness.MineAsync();
        Assert.Equal(OutputResolutionState.Resolved, harness.Rows[(child.TransactionId, child.OutputIndex)].State);
        Assert.Equal(broadcast.TransactionId, harness.Watches[(child.TransactionId, child.OutputIndex)].SpentByTransactionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ReconfirmedSameParent_When_InitialSweepResumes_Then_OldIntentRetiresWithoutSigningAndFreshCsvIsEvaluated(bool secondLevel)
    {
        // Arrange
        using var harness = await CreateMatureOutputAsync(false, secondLevel);
        var row = FindOutput(harness, secondLevel);
        var data = OutputDescriptorData.TryDecode(row)!;
        var builder = harness.GetService<ISweepTransactionBuilder>();
        var unsigned = builder.BuildWithFee([new SweepInput(row.TransactionId, row.OutputIndex, data.AmountSat,
            SweepSpendKind.DelayedOutput, data.WitnessScript, data.CsvDelay,
            PerCommitmentPoint: data.PerCommitmentPoint)], harness.Destination, 1_000);
        var fake = new Capture(harness.GetService<ILightningSigner>()) { LoseReply = true };
        var intent = WithParent(harness, new InitialDelayedSweepIntent(unsigned, row, harness.Close, harness.Height,
            1_000, harness.Channel.GetSigningInfo(), true));
        var workflow = new InitialDelayedSweepWorkflow(fake.Coordinator.Object, fake.Recovery.Object, builder, fake.Signer);
        await Assert.ThrowsAsync<IOException>(() => workflow.StartAsync(intent));
        var reconfirmed = harness.Close;
        var newBlock = new Hash(Enumerable.Repeat((byte)7, 32).ToArray());
        if (secondLevel)
        {
            var parent = intent.Parent!;
            harness.Watches[(parent.Output.TransactionId, parent.Output.OutputIndex)]
                .MarkSpent(row.TransactionId, harness.Height, newBlock);
        }
        else
            reconfirmed = harness.Close with { SpentAtHeight = harness.Height, BlockHash = newBlock };
        fake.Recovery.Invocations.Clear();

        // Act
        var actions = await workflow.ResumeAsync(reconfirmed, harness.Rows.Values.ToList(), harness.Height,
            harness.CreateUnitOfWorkForTests());

        // Assert: changed confirmation retires the old exact request without replaying a private signature.
        Assert.NotNull(actions);
        Assert.Empty(actions.OfType<BroadcastAction>());
        fake.Recovery.Verify(r => r.SignSweepInputs(It.IsAny<ISigningWorkflowScope>(), It.IsAny<ILightningSigner>()), Times.Never);
        await Assert.Single(actions.OfType<StageWriteAction>()).Stage(harness.CreateUnitOfWorkForTests(), TestContext.Current.CancellationToken);
        Assert.True(fake.Retired);
        Assert.Null(fake.Saved);
        if (secondLevel)
        {
            var toLocal = FindOutput(harness, false);
            harness.Rows[(toLocal.TransactionId, toLocal.OutputIndex)] = toLocal with
            { State = OutputResolutionState.Broadcast, ResolvingTransactionId = new Domain.Bitcoin.ValueObjects.TxId(Enumerable.Repeat((byte)8, 32).ToArray()) };
        }
        var resolver = new Application.Onchain.Resolvers.LocalCommitResolver(harness.GetService<ICommitmentOutputMapper>(),
            harness.GetService<IHtlcTransactionBuilder>(), builder, fake.Signer, harness.GetService<IFeeService>(),
            harness.GetService<ISweepDestinationProvider>(), harness.GetService<IServiceScopeFactory>(),
            NullLogger<Application.Onchain.Resolvers.LocalCommitResolver>.Instance,
            chainService: harness.GetService<Infrastructure.Bitcoin.Wallet.Interfaces.IBitcoinChainService>(),
            signingWorkflows: fake.Coordinator.Object);
        var next = await resolver.ResolveAsync(reconfirmed, harness.Rows.Values.ToList(), harness.Height,
            TestContext.Current.CancellationToken);
        Assert.Empty(next.OfType<BroadcastAction>());
        Assert.Equal(1, fake.SignExecutions); // The renewed parent CSV has not matured yet.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_MatureLocalOutput_When_ResolverPlansInitialSweep_Then_OneWorkflowOwnsTheExecutorRound(bool taproot)
    {
        // Arrange
        using var harness = await CreateMatureOutputAsync(taproot, false);
        var fake = new Capture(harness.GetService<ILightningSigner>());
        var resolver = new Application.Onchain.Resolvers.LocalCommitResolver(
            harness.GetService<ICommitmentOutputMapper>(), harness.GetService<IHtlcTransactionBuilder>(),
            harness.GetService<ISweepTransactionBuilder>(), fake.Signer, harness.GetService<IFeeService>(),
            harness.GetService<ISweepDestinationProvider>(),
            harness.GetService<IServiceScopeFactory>(), NullLogger<Application.Onchain.Resolvers.LocalCommitResolver>.Instance,
            signingWorkflows: fake.Coordinator.Object);

        // Act
        var actions = await resolver.ResolveAsync(harness.Close, harness.Rows.Values.ToList(), harness.Height,
            TestContext.Current.CancellationToken);

        // Assert: capture occurs in the resolver but consumption waits for the executor's unit of work.
        Assert.Single(actions.OfType<SigningWorkflowRoundAction>());
        Assert.Single(actions.OfType<BroadcastAction>());
        Assert.False(fake.Consumed);
        var executorUnitOfWork = harness.CreateUnitOfWorkForTests();
        await Assert.Single(actions.OfType<StageWriteAction>()).Stage(executorUnitOfWork, TestContext.Current.CancellationToken);
        Assert.True(fake.Consumed);
        Assert.Same(executorUnitOfWork, fake.ConsumptionUnitOfWork);
        Assert.Equal(1, fake.SignExecutions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Given_ReplyLost_When_InitialDelayedSweepResumes_Then_ExactWitnessAndDecisionCommitTogether(
        bool taproot, bool secondLevel)
    {
        // Arrange: actual local delayed-output scripts, including the output of a confirmed HTLC-success.
        using var harness = await CreateMatureOutputAsync(taproot, secondLevel);
        var row = FindOutput(harness, secondLevel);
        var data = OutputDescriptorData.TryDecode(row)!;
        var builder = harness.GetService<ISweepTransactionBuilder>();
        var unsigned = builder.BuildWithFee([new SweepInput(row.TransactionId, row.OutputIndex, data.AmountSat,
            SweepSpendKind.DelayedOutput, data.WitnessScript, data.CsvDelay,
            PerCommitmentPoint: data.PerCommitmentPoint, TaprootControlBlock: data.TaprootControlBlock,
            SpentScriptPubKey: data.ScriptPubKey)], harness.Destination, 1_000);
        var intent = WithParent(harness, new InitialDelayedSweepIntent(unsigned, row, harness.Close, harness.Height, 1_000, harness.Channel.GetSigningInfo(), true));
        var fake = new Capture(harness.GetService<ILightningSigner>());
        var workflow = new InitialDelayedSweepWorkflow(fake.Coordinator.Object, fake.Recovery.Object, builder, fake.Signer);
        fake.LoseReply = true;
        await Assert.ThrowsAsync<IOException>(() => workflow.StartAsync(intent));
        Assert.NotNull(fake.Saved);
        Assert.False(fake.Consumed);
        var originalEnvelopeIntent = fake.Saved!.PublicationIntent;
        fake.LoseReply = false;
        var uow = harness.CreateUnitOfWorkForTests();

        // Act: the restarted workflow uses its saved unsigned transaction and contexts.
        var resumed = new InitialDelayedSweepWorkflow(fake.Coordinator.Object, fake.Recovery.Object, builder, fake.Signer);
        var actions = await resumed.ResumeAsync(harness.Close, harness.Rows.Values.ToList(), harness.Height + 1, uow);

        // Assert: no node state is consumed until the executor stages the exact signed broadcast and output decision.
        Assert.NotNull(actions);
        var broadcast = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.False(fake.Consumed);
        Assert.Equal(unsigned.Transaction.TxId, broadcast.TransactionId);
        Assert.Equal(harness.Height, broadcast.FirstBroadcastHeight);
        Assert.Equal(1_000, broadcast.Fee!.Satoshi);
        var finalRow = Assert.Single(actions.OfType<UpsertOutputAction>()).Output;
        Assert.Equal(broadcast.TransactionId, finalRow.ResolvingTransactionId);
        Assert.Equal(OutputResolutionState.Broadcast, finalRow.State);
        Assert.Null(finalRow.WaitUntilHeight);
        Assert.Single(actions.OfType<SigningWorkflowRoundAction>());
        harness.AssertAllInputsVerify(Transaction.Load(broadcast.RawTransaction, Network.Main));
        await Assert.Single(actions.OfType<StageWriteAction>()).Stage(uow, TestContext.Current.CancellationToken);
        Assert.True(fake.Consumed);
        Assert.Same(uow, fake.ConsumptionUnitOfWork);
        Assert.Equal(originalEnvelopeIntent, fake.Saved.PublicationIntent);
        Assert.Equal(1, fake.SignExecutions); // Receipt replay does not privately sign again.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_CompetingSpend_When_InitialSweepResumes_Then_RetirementDefersToExecutorWithoutSigning(bool taproot)
    {
        // Arrange
        using var harness = await CreateMatureOutputAsync(taproot, false);
        var row = FindOutput(harness, false);
        var data = OutputDescriptorData.TryDecode(row)!;
        var builder = harness.GetService<ISweepTransactionBuilder>();
        var unsigned = builder.BuildWithFee([new SweepInput(row.TransactionId, row.OutputIndex, data.AmountSat,
            SweepSpendKind.DelayedOutput, data.WitnessScript, data.CsvDelay,
            PerCommitmentPoint: data.PerCommitmentPoint, TaprootControlBlock: data.TaprootControlBlock,
            SpentScriptPubKey: data.ScriptPubKey)], harness.Destination, 1_000);
        var fake = new Capture(harness.GetService<ILightningSigner>()) { LoseReply = true };
        var workflow = new InitialDelayedSweepWorkflow(fake.Coordinator.Object, fake.Recovery.Object, builder, fake.Signer);
        await Assert.ThrowsAsync<IOException>(() => workflow.StartAsync(new InitialDelayedSweepIntent(unsigned,
            row, harness.Close, harness.Height, 1_000, harness.Channel.GetSigningInfo(), true)));
        var resolved = harness.Rows.Values.Select(current => current.TransactionId == row.TransactionId
            && current.OutputIndex == row.OutputIndex ? current with { State = OutputResolutionState.Resolved } : current).ToList();
        fake.Recovery.Invocations.Clear();
        var uow = harness.CreateUnitOfWorkForTests();

        // Act
        var actions = await workflow.ResumeAsync(harness.Close, resolved, harness.Height, uow);

        // Assert: the executor gets a real staged write, never an obsolete transaction to publish.
        Assert.NotNull(actions);
        Assert.Empty(actions.OfType<BroadcastAction>());
        Assert.False(fake.Retired);
        fake.Recovery.Verify(r => r.SignSweepInputs(It.IsAny<ISigningWorkflowScope>(), It.IsAny<ILightningSigner>()), Times.Never);
        await Assert.Single(actions.OfType<StageWriteAction>()).Stage(uow, TestContext.Current.CancellationToken);
        Assert.True(fake.Retired);
        Assert.Null(fake.Saved);
    }

    [Fact]
    public async Task Given_ChangedDescriptor_When_InitialSweepResumes_Then_NoSigningOrPublicationOccurs()
    {
        // Arrange
        using var harness = await CreateMatureOutputAsync(false, false);
        var row = FindOutput(harness, false);
        var data = OutputDescriptorData.TryDecode(row)!;
        var builder = harness.GetService<ISweepTransactionBuilder>();
        var unsigned = builder.BuildWithFee([new SweepInput(row.TransactionId, row.OutputIndex, data.AmountSat,
            SweepSpendKind.DelayedOutput, data.WitnessScript, data.CsvDelay,
            PerCommitmentPoint: data.PerCommitmentPoint)], harness.Destination, 1_000);
        var fake = new Capture(harness.GetService<ILightningSigner>()) { LoseReply = true };
        var workflow = new InitialDelayedSweepWorkflow(fake.Coordinator.Object, fake.Recovery.Object, builder, fake.Signer);
        await Assert.ThrowsAsync<IOException>(() => workflow.StartAsync(new InitialDelayedSweepIntent(unsigned,
            row, harness.Close, harness.Height, 1_000, harness.Channel.GetSigningInfo(), true)));
        var changed = harness.Rows.Values.Select(current => current.TransactionId == row.TransactionId
            && current.OutputIndex == row.OutputIndex ? current with { DescriptorData = [1] } : current).ToList();
        fake.Recovery.Invocations.Clear();

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ResumeAsync(harness.Close, changed,
            harness.Height, harness.CreateUnitOfWorkForTests()));
        fake.Recovery.Verify(r => r.SignSweepInputs(It.IsAny<ISigningWorkflowScope>(), It.IsAny<ILightningSigner>()), Times.Never);
        Assert.False(fake.Consumed);
    }

    private static InitialDelayedSweepIntent WithParent(LocalCommitResolutionHarness harness, InitialDelayedSweepIntent intent)
    {
        if (intent.Output.TransactionId == harness.Close.CommitmentTransactionId)
            return intent;
        var parent = Assert.Single(harness.Rows.Values, row => row.TransactionId == harness.Close.CommitmentTransactionId
            && row.ResolvingTransactionId == intent.Output.TransactionId);
        var watch = harness.Watches[(parent.TransactionId, parent.OutputIndex)];
        return intent with
        {
            Parent = new InitialDelayedSweepParent(parent, watch.SpentAtHeight!.Value,
            watch.SpentBlockHash ?? Hash.Empty)
        };
    }

    private static OutputResolutionModel FindOutput(LocalCommitResolutionHarness harness, bool secondLevel)
        => Assert.Single(harness.Rows.Values, row => row.Descriptor == OutputDescriptorKind.DelayedToLocal
            && (row.TransactionId != harness.Close.CommitmentTransactionId) == secondLevel);

    private static async Task<LocalCommitResolutionHarness> CreateMatureOutputAsync(bool taproot, bool secondLevel)
    {
        var wallet = secondLevel && taproot ? new AnchorTestWallet(true, 60_000) : null;
        var preimage = RealSigningCommitmentPair.Preimage(2);
        var harness = new LocalCommitResolutionHarness(secondLevel ? pair =>
        {
            var id = pair.Add(pair.Bob, 30_000_000, preimage, 1_020);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, preimage,
                new Infrastructure.Crypto.Hashes.Sha256()));
        }
        : null, feeInputProvider: wallet, simpleTaproot: taproot);
        if (wallet is not null)
            foreach (var funding in wallet.FundingTransactions)
                harness.AddKnownTransaction(funding);
        await harness.ResolveAsync();
        if (secondLevel)
            await harness.MineAsync();
        harness.ResolveEachBlock = false;
        await harness.MineToAsync(secondLevel ? harness.Height + Csv : CloseHeight + Csv);
        return harness;
    }

    private sealed class Capture
    {
        public Mock<IRemoteSigningWorkflowCoordinator> Coordinator { get; } = new();
        public Mock<INativeInitialSweepSigningRecovery> Recovery { get; }
        public ILightningSigner Signer { get; }
        public SigningWorkflowDescriptor? Saved { get; private set; }
        public bool LoseReply { get; set; }
        public bool Consumed { get; private set; }
        public bool Retired { get; private set; }
        public int SignExecutions { get; private set; }
        public IUnitOfWork? ConsumptionUnitOfWork { get; private set; }
        private readonly Guid _workflowId = Guid.NewGuid();
        public InitialDelayedSweepIntent? Intent { get; private set; }
        private IReadOnlyList<CompactSignature>? _receipts;

        public Capture(ILightningSigner signer)
        {
            Signer = signer;
            Recovery = Coordinator.As<INativeInitialSweepSigningRecovery>();
            var scope = new Mock<ISigningWorkflowScope>();
            scope.SetupGet(s => s.WorkflowId).Returns(_workflowId);
            scope.Setup(s => s.StageConsumeAsync(It.IsAny<IUnitOfWork>())).Returns((IUnitOfWork uow) =>
            { Consumed = true; ConsumptionUnitOfWork = uow; return Task.CompletedTask; });
            Coordinator.Setup(c => c.BeginAsync(It.IsAny<SigningWorkflowDescriptor>())).ReturnsAsync((SigningWorkflowDescriptor descriptor) =>
            {
                if (Saved is not null)
                    Assert.Equal(Saved.PublicationIntent, descriptor.PublicationIntent);
                Saved = descriptor;
                return scope.Object;
            });
            Coordinator.Setup(c => c.GetPendingAsync(It.IsAny<ChannelId>())).ReturnsAsync(() => Saved is null ? [] :
                [new SigningWorkflow(_workflowId, Saved.ChannelId, Saved.Kind, 0, 0, Saved.SnapshotFingerprint,
                    [], "regtest", 1, SigningWorkflowState.Pending, 0, 0) { PublicationIntent = Saved.PublicationIntent }]);
            Recovery.Setup(r => r.EncodeInitialSweepIntent(It.IsAny<InitialDelayedSweepIntent>())).Returns((InitialDelayedSweepIntent intent) =>
            { Intent = intent; return [1, 2, 3]; });
            Recovery.Setup(r => r.DecodeInitialSweepIntent(It.IsAny<byte[]>())).Returns(() => Intent!);
            Recovery.Setup(r => r.SignSweepInputs(It.IsAny<ISigningWorkflowScope>(), It.IsAny<ILightningSigner>())).Returns(() =>
            {
                Assert.NotNull(Saved);
                if (_receipts is null)
                {
                    SignExecutions++;
                    _receipts = [signer.SignSweepInput(Saved.ChannelId, Intent!.Transaction.GetSigningContext(0))];
                }
                if (LoseReply)
                    throw new IOException("Signer committed before the reply arrived.");
                return _receipts;
            });
            Recovery.Setup(r => r.StageRetireSweepAsync(It.IsAny<SigningWorkflowDescriptor>(), It.IsAny<IUnitOfWork>()))
                .Returns((SigningWorkflowDescriptor descriptor, IUnitOfWork _) =>
                { Assert.Equal(Saved!.PublicationIntent, descriptor.PublicationIntent); Retired = true; Saved = null; _receipts = null; return Task.CompletedTask; });
        }
    }
}