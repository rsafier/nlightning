namespace NLightning.Application.Tests.Channels.Splicing;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Harness;

/// <summary>
/// NL-867 on the splice RBF path (<see cref="SpliceHarness"/> on the real engine): once a pending attempt of the splice
/// has a confirmation (below the lock depth), every other attempt double-spends it and can never confirm. A bump is
/// refused before any quiescence, a running RBF attempt is abandoned with <c>tx_abort</c> at the block that confirmed
/// its sibling, and an attempt whose wallet inputs the chain spent is aborted instead of failing the connection.
/// </summary>
public class SpliceRbfConfirmedAttemptTests
{
    private const long SpliceIn = 100_000;

    [Fact]
    public async Task Given_APendingSpliceWithAConfirmation_When_Bumping_Then_RefusedBeforeAnyQuiescence()
    {
        // Arrange: the splice is in a block, below the lock depth (no splice_locked yet)
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        MarkFirstSeen(harness.Alice, first);
        var mark = harness.Transcript.Count;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000),
                                                  TestContext.Current.CancellationToken));
        Assert.Contains("already has a confirmation", exception.Message);
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t.Message is StfuMessage);
    }

    [Fact]
    public async Task Given_ASpliceRbfBeingNegotiated_When_ThePendingSpliceConfirms_Then_AbandonedWithTxAbort()
    {
        // Arrange: Alice bumps her splice-in; the attempt is between its tx_add messages
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        var bump = harness.Alice.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000),
                                                   TestContext.Current.CancellationToken);
        await DeliverUntilAsync(harness, m => m is TxCompleteMessage);

        // Act: the pending splice is mined (both monitors), and the wallet saw Alice's input spent
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            MarkFirstSeen(node, first);
            MarkWalletSpent(node, first);
            node.Node.ChainMonitor.Raise(m => m.OnNewBlockDetected += null,
                                         new NewBlockEventArgs(TwoNodeHarness.BlockHeight + 1, new Hash(new byte[32])));
        }

        await harness.PumpAsync(bump);

        // Assert: our tx_abort, the bump answered, only the first splice pending, nothing failed, quiescence over
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains($"an earlier attempt {first} confirmed", result.FailureReason);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxAbortMessage });
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal([first], node.Node.State.PendingFundings.Select(f => f.FundingTxId));
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        }
    }

    [Fact]
    public async Task Given_TheWalletSawOurInputSpent_When_SigningTheSpliceRbf_Then_TxAbortInsteadOfAFailure()
    {
        // Arrange: no block event yet, only Alice's wallet knows her splice-in input was spent
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        MarkWalletSpent(harness.Alice, first);
        var mark = harness.Transcript.Count;

        // Act
        var bump = harness.Alice.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000),
                                                   TestContext.Current.CancellationToken);
        await harness.PumpAsync(bump);

        // Assert: aborted before Alice's tx_signatures, the first splice still her only pending one (Bob, who signed
        // first, keeps the attempt after his tx_signatures, IT-ABT-01: it double-spends the first and never confirms)
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotEqual(SpliceNegotiationState.Signed, result.State);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxAbortMessage });
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t is { From: "Alice", Message: TxSignaturesMessage });
        Assert.Empty(harness.Failures);
        Assert.Equal([first], harness.Alice.Node.State.PendingFundings.Select(f => f.FundingTxId));
    }

    private static SpliceHarness CreateHarness() =>
        new((_, o) => o.MinRbfInterval = TimeSpan.Zero, realEngine: true);

    /// <summary>The chain monitor of <paramref name="node"/> found <paramref name="txId"/> in a block (its watch).</summary>
    private static void MarkFirstSeen(SpliceNode node, TxId txId) =>
        node.Watches.Single(w => w.TransactionId == txId).SetHeightAndIndex(TwoNodeHarness.BlockHeight + 1, 1);

    /// <summary>The wallet of <paramref name="node"/> saw the inputs of <paramref name="txId"/> spent.</summary>
    private static void MarkWalletSpent(SpliceNode node, TxId txId)
    {
        var session = node.Sessions.Committed.Values.Single(s => s.ConstructedTx?.TxId == txId);
        foreach (var input in session.ConstructedTx!.Inputs)
            node.Contributor.SpentOnChain.Add((input.PrevTxId, input.PrevTxVout));
    }

    /// <summary>Delivers one message at a time, both ways, until the next one matches <paramref name="stopBefore"/>.</summary>
    private static async Task DeliverUntilAsync(SpliceHarness harness, Func<IChannelMessage, bool> stopBefore)
    {
        for (var step = 0; step < 1_000; step++)
        {
            await harness.WhenIdleAsync();
            var delivered = false;
            foreach (var node in new[] { harness.Harness.Alice, harness.Harness.Bob })
            {
                if (node.PeekNext() is { } next && stopBefore(next))
                    return;

                delivered |= await node.DeliverNextAsync();
            }

            if (!delivered)
                await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("The awaited message never came");
    }
}