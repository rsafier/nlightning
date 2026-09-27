using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Safety;
using Application.Channels.Safety.Interfaces;
using Channels.Harness;
using Channels.Splicing;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Onchain.Enums;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Splicing plan §3.6, SP2-C-T2 (SP-I4): a channel fails while a splice is pending, and the splice confirms instead of
/// our commitment on the funding it spends; the failure service then signs our latest commitment on the splice funding
/// (the same number, with the peer's real signature of it from the batched <c>commitment_signed</c>) and stores its
/// broadcast row. Real engines and signers (<see cref="SpliceHarness"/> with the production splice state port).
/// </summary>
public sealed class OnchainSpliceForceCloseTests
{
    [Fact]
    public async Task Given_AFailedChannelWithAPendingSplice_When_TheSpliceConfirmed_Then_OurCommitmentOnItIsValid()
    {
        // Arrange: Alice splices 100,000 sat in; the splice is signed and pending, a payment makes a new commitment
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var spliceTxId = result.SpliceTxId!.Value;
        var channel = harness.Alice.Node.Channel;
        var splice = channel.Commitments!.PendingFundings.Single(f => f.FundingTxId == spliceTxId);
        var failure = CreateFailureService(harness.Alice);
        channel.UpdateState(ChannelState.Failed);

        // Act
        var outcome = await failure.BroadcastOnSpliceAsync(channel.ChannelId, spliceTxId,
                                                           TestContext.Current.CancellationToken);

        // Assert: one LocalCommitment row at the channel's local number, spending the splice output with a valid
        // 2-of-2 witness (script-executed against the splice transaction's output)
        Assert.NotNull(outcome.CommitmentTxId);
        var row = Assert.Single(harness.Alice.Broadcasts, b => b.Purpose == BroadcastPurpose.LocalCommitment);
        Assert.Equal(outcome.CommitmentTxId, row.TransactionId);
        Assert.Equal(channel.Commitments.LocalCommit.Number, row.CommitmentNumber);
        var commitment = Transaction.Load(row.RawTransaction, Network.RegTest);
        var input = Assert.Single(commitment.Inputs);
        Assert.Equal(new OutPoint(new uint256((byte[])spliceTxId), splice.OutputIndex), input.PrevOut);
        var spliceTx = Transaction.Load(harness.Alice.Broadcasts.Single(b => b.TransactionId == spliceTxId)
                                              .RawTransaction, Network.RegTest);
        var spent = spliceTx.Outputs[splice.OutputIndex];
        Assert.Equal((long)splice.CapacitySatoshis, spent.Value.Satoshi);
        Assert.True(commitment.Inputs.AsIndexedInputs().Single().VerifyScript(spent, out var error),
                    $"our commitment on the splice funding does not verify: {error}");

        // A second call builds the same transaction (the signer allows the same number again, SP-I4)
        var again = await failure.BroadcastOnSpliceAsync(channel.ChannelId, spliceTxId,
                                                         TestContext.Current.CancellationToken);
        Assert.Equal(outcome.CommitmentTxId, again.CommitmentTxId);
    }

    [Fact]
    public async Task Given_AnOpenChannel_When_AskedToBroadcastOnItsSplice_Then_NotApplicable()
    {
        // Arrange: nothing failed; a splice commitment is never broadcast for a working channel
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var failure = CreateFailureService(harness.Alice);

        // Act
        var outcome = await failure.BroadcastOnSpliceAsync(harness.Alice.Node.Channel.ChannelId,
                                                           result.SpliceTxId!.Value,
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.NotApplicable, outcome.Status);
        Assert.DoesNotContain(harness.Alice.Broadcasts, b => b.Purpose == BroadcastPurpose.LocalCommitment);
    }

    [Fact]
    public async Task Given_AFailedChannel_When_ItsSpliceWatchHasABlock_Then_TheBlockCheckBroadcastsOnTheSplice()
    {
        // Arrange: the splice's WatchedTransactions row got its block (the failure service checks it every block)
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var spliceTxId = result.SpliceTxId!.Value;
        var channel = harness.Alice.Node.Channel;
        var failure = CreateFailureService(harness.Alice);
        channel.UpdateState(ChannelState.Failed);
        harness.Alice.Watches.Single(w => w.TransactionId == spliceTxId)
               .SetHeightAndIndex(TwoNodeHarness.BlockHeight + 1, 1);

        // Act
        await failure.CheckConfirmedSplicesAsync(TestContext.Current.CancellationToken);
        await failure.CheckConfirmedSplicesAsync(TestContext.Current.CancellationToken);

        // Assert: once
        var row = Assert.Single(harness.Alice.Broadcasts, b => b.Purpose == BroadcastPurpose.LocalCommitment);
        var commitment = Transaction.Load(row.RawTransaction, Network.RegTest);
        Assert.Equal(new uint256((byte[])spliceTxId), commitment.Inputs.Single().PrevOut.Hash);
    }

    private static ChannelFailureService CreateFailureService(SpliceNode node)
    {
        var services = node.Node.Services;
        var errors = new Mock<IChannelErrorSender>();
        return new ChannelFailureService(node.Node.ChainMonitor.Object, errors.Object,
                                         services.GetRequiredService<IChannelLockProvider>(),
                                         services.GetRequiredService<IChannelMemoryRepository>(),
                                         new LocalCommitmentBroadcastBuilder(
                                             services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                             services.GetRequiredService<ICommitmentTransactionBuilder>(),
                                             services.GetRequiredService<ILightningSigner>()),
                                         services.GetRequiredService<ILightningSigner>(),
                                         NullLogger<ChannelFailureService>.Instance,
                                         services.GetRequiredService<IServiceScopeFactory>(), null,
                                         services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                         services.GetRequiredService<ICommitmentTransactionBuilder>());
    }
}