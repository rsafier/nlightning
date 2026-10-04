using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Harness;
using NLightning.Tests.Utils;

/// <summary>
/// NL-983 in process: with <c>option_simple_close</c> each side proposes its own closing transaction and signs the
/// peer's, so the funding output can be spent by one of them while the <c>closing_sig</c> of the other is still in
/// flight. The late <c>closing_sig</c> (first close and RBF round, legacy anchors-shaped and simple taproot channels,
/// either side late) must not replace the closing transaction that already spends the funding output, in a block or in
/// the mempool, and the confirmed one closes the channel.
/// </summary>
public class LateClosingSigHarnessTests
{
    private const uint SpendHeight = TwoNodeHarness.BlockHeight + 1;

    public static TheoryData<bool, bool> Cases => new()
    {
        { false, true },
        { false, false },
        { true, true },
        { true, false }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Given_ThePeersClosingTxConfirmed_When_OurClosingSigArrivesLate_Then_TheConfirmedTxClosesTheChannel(
        bool simpleTaproot, bool aliceIsLate)
    {
        // Arrange: the late side signed the other side's transaction (stored, broadcast); a block holds it
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: simpleTaproot);
        var (late, _) = Roles(close, aliceIsLate);
        var held = await RunHoldingClosingSigAsync(close, late, () => StartCloseAsync(close));
        var othersTx = late.Channel.ClosingTransaction!;
        Assert.Single(close.Published(late), t => t.TxId == othersTx.TxId);
        SeenInBlock(late, othersTx);

        // Act
        await DeliverAsync(late, held);

        // Assert: the late closing_sig completed and broadcast our transaction, but the confirmed one stays recorded
        Assert.Equal(2, close.Published(late).Count);
        Assert.Equal(othersTx.TxId, late.Channel.ClosingTransaction!.TxId);
        await AssertClosedByAsync(late, othersTx);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Given_ThePeersClosingTxInTheMempool_When_OurClosingSigArrivesLate_Then_ItStaysRecordedAndCloses(
        bool simpleTaproot, bool aliceIsLate)
    {
        // Arrange: the other side's transaction spends the funding output in the mempool; ours pays the same fee
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: simpleTaproot);
        var (late, _) = Roles(close, aliceIsLate);
        var held = await RunHoldingClosingSigAsync(close, late, () => StartCloseAsync(close));
        var othersTx = late.Channel.ClosingTransaction!;
        await SeenInMempoolAsync(late, othersTx);

        // Act
        await DeliverAsync(late, held);

        // Assert
        Assert.Equal(othersTx.TxId, late.Channel.ClosingTransaction!.TxId);
        var ours = Assert.Single(close.Published(late), t => t.TxId != othersTx.TxId);
        Assert.Equal(TotalOut(othersTx), TotalOut(ours));
        await AssertClosedByAsync(late, othersTx);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Given_TheirBumpConfirmedInAnRbfRound_When_OurBumpsClosingSigArrivesLate_Then_TheirBumpCloses(
        bool simpleTaproot, bool aliceIsLate)
    {
        // Arrange: a completed close, then both sides bump at once; the late side signed the other's bump, which
        // confirms before the late side's own bump is signed back
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: simpleTaproot);
        var ct = TestContext.Current.CancellationToken;
        var (late, other) = Roles(close, aliceIsLate);
        await StartCloseAsync(close);
        await close.Harness.PumpAsync();
        var held = await RunHoldingClosingSigAsync(close, late, async () =>
        {
            await close.CloseService(late)
                       .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 12_000), ct);
            await close.CloseService(other)
                       .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 10_000), ct);
        });
        var othersBump = late.Channel.ClosingTransaction!;
        Assert.Equal(3, close.Published(late).Count);
        Assert.Equal(othersBump.TxId, close.Published(late)[^1].TxId);
        SeenInBlock(late, othersBump);

        // Act
        await DeliverAsync(late, held);

        // Assert: our bump pays more, but the confirmed transaction is final
        Assert.Equal(4, close.Published(late).Count);
        Assert.True(TotalOut(close.Published(late)[^1]) < TotalOut(othersBump));
        Assert.Equal(othersBump.TxId, late.Channel.ClosingTransaction!.TxId);
        await AssertClosedByAsync(late, othersBump);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Given_ThePeersClosingTxInTheMempool_When_OurHigherFeeBumpIsSigned_Then_ItReplacesTheStoredTx(
        bool simpleTaproot, bool aliceIsLate)
    {
        // Arrange: a completed close, the other side's transaction in the mempool
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: simpleTaproot);
        var ct = TestContext.Current.CancellationToken;
        var (late, other) = Roles(close, aliceIsLate);
        await StartCloseAsync(close);
        await close.Harness.PumpAsync();
        var othersTx = ProposalOf(close, other);
        await SeenInMempoolAsync(late, othersTx);

        // Act: an RBF of ours pays more fee, so it can replace the one in the mempool
        await close.CloseService(late)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 10_000), ct);
        await close.Harness.PumpAsync();

        // Assert: the bump is the stored closing transaction
        var bump = close.Published(late)[^1];
        Assert.True(TotalOut(bump) < TotalOut(othersTx));
        Assert.Equal(bump.TxId, late.Channel.ClosingTransaction!.TxId);
    }

    #region Helpers

    private static (HarnessNode Late, HarnessNode Other) Roles(CloseHarness close, bool aliceIsLate) =>
        aliceIsLate ? (close.Alice, close.Bob) : (close.Bob, close.Alice);

    private static Task StartCloseAsync(CloseHarness close) =>
        close.CloseService(close.Alice)
             .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs <paramref name="start"/> and delivers every message both ways, except the <c>closing_sig</c>s sent to
    /// <paramref name="late"/>, which are held; returns the one held.
    /// </summary>
    private static async Task<IChannelMessage> RunHoldingClosingSigAsync(CloseHarness close, HarnessNode late,
                                                                         Func<Task> start)
    {
        await start();
        var held = new List<IChannelMessage>();
        for (var steps = 0; steps < 1_000; steps++)
        {
            var delivered = false;
            foreach (var node in new[] { close.Alice, close.Bob })
            {
                if (node == late.Peer && node.PeekNext() is ClosingSigMessage)
                {
                    Assert.True(node.TryTakeNext(out var message));
                    held.Add(message);
                    delivered = true;
                }
                else
                {
                    delivered |= await node.DeliverNextAsync();
                }
            }

            if (!delivered)
                return Assert.Single(held);
        }

        throw new InvalidOperationException("The close did not converge");
    }

    private static Task DeliverAsync(HarnessNode late, IChannelMessage message) =>
        late.ChannelManager.HandleChannelMessageAsync(message, late.Peer.NegotiatedFeatures, late.Peer.NodeId);

    /// <summary>The block-processing save marked <paramref name="tx"/>'s watch first seen (NL-983's confirmed case).</summary>
    private static void SeenInBlock(HarnessNode node, SignedTransaction tx)
    {
        var watch = new WatchedTransactionModel(TwoNodeHarness.ChannelId, tx.TxId, 6);
        watch.SetHeightAndIndex(SpendHeight, 1);
        node.WatchedTransactions.Setup(r => r.GetByTransactionIdAsync(tx.TxId)).ReturnsAsync(watch);
    }

    /// <summary>The chain monitor reports <paramref name="tx"/> spending the funding output in the mempool.</summary>
    private static async Task SeenInMempoolAsync(HarnessNode node, SignedTransaction tx)
    {
        var funding = node.Channel.FundingOutput!;
        node.ChainMonitor.Raise(m => m.OnWatchedOutpointSpentInMempool += null, node.ChainMonitor.Object,
                                new MempoolSpendEventArgs(TwoNodeHarness.ChannelId, tx, funding.TransactionId!.Value,
                                                          funding.Index!.Value, false));
        var registry = node.Services.GetRequiredService<ClosingNegotiationRegistry>();
        await WaitFor.TrueAsync(() => registry.TryGet(TwoNodeHarness.ChannelId, out var entry)
                                   && entry!.MempoolFundingSpend?.TxId == tx.TxId,
                                TimeSpan.FromSeconds(10), "the mempool spend noted",
                                TestContext.Current.CancellationToken);
    }

    /// <summary><paramref name="tx"/> reaches its depth: the channel is Closed with it as its closing transaction.</summary>
    private static async Task AssertClosedByAsync(HarnessNode node, SignedTransaction tx)
    {
        var channel = node.Channel;
        var watch = new WatchedTransactionModel(TwoNodeHarness.ChannelId, tx.TxId, 6);
        watch.SetHeightAndIndex(SpendHeight, 1);
        watch.MarkAsCompleted();
        node.ChainMonitor.Raise(m => m.OnTransactionConfirmed += null, node.ChainMonitor.Object,
                                new TransactionConfirmedEventArgs(watch, SpendHeight + 5));

        await WaitFor.TrueAsync(() => channel.State == ChannelState.Closed, TimeSpan.FromSeconds(10),
                                "the channel Closed", TestContext.Current.CancellationToken);
        Assert.Equal(tx.TxId, channel.ClosingTransaction!.TxId);
    }

    /// <summary>
    /// The closing transaction <paramref name="closer"/> proposed (and paid the fee of): the one whose output to its
    /// peer is the peer's whole share.
    /// </summary>
    private static SignedTransaction ProposalOf(CloseHarness close, HarnessNode closer)
    {
        var peerIsAlice = closer.Peer == close.Alice;
        var peerScript = peerIsAlice ? CloseHarness.AliceScript : CloseHarness.BobScript;
        var peerShare = (long)(peerIsAlice
                                   ? TwoNodeHarness.FundingSatoshis - TwoNodeHarness.PushSatoshis
                                   : TwoNodeHarness.PushSatoshis);
        return close.Published(closer)
                    .Single(t => CloseHarness.OutputTo(Transaction.Load(t.RawTxBytes, Network.RegTest), peerScript)
                              == peerShare);
    }

    private static long TotalOut(SignedTransaction tx) =>
        Transaction.Load(tx.RawTxBytes, Network.RegTest).TotalOut.Satoshi;

    #endregion
}