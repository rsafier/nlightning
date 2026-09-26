using Lnrpc;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Payments.Enums;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan O7-T2 proof (B5-FAIL-06): our commitment of an anchors channel whose feerate is far below the fee
/// estimate is fee-bumped by a child that spends our anchor (<c>to_local_anchor</c>) and wallet inputs (CPFP), against
/// LND david:
/// <list type="bullet">
///   <item>(a) a channel opened at BOLT 2's floor (253 sat/kw, about 1 sat/vB) while the estimate is 10 sat/vB: at our
///   force close a child spending our anchor and at least one wallet input is in the mempool, the package (commitment
///   and child) pays at least the estimate, and both confirm in the same block; the child's change is ours.</item>
///   <item>(b) the same with our HTLC in the commitment (a deadline): while the miners leave the package out (empty
///   blocks through <c>generateblock</c>), the child is replaced by one with a higher fee (RBF of the child), and the
///   commitment confirms before the HTLC's <c>cltv_expiry</c> once blocks take it.</item>
/// </list>
/// </summary>
/// <remarks>
/// The child is found in bitcoind's mempool as the transaction that spends our anchor, so the proof holds whatever
/// shape the CPFP service (lane O7-X2) takes. Run with <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsCpfpTests : IAsyncLifetime
{
    private const ulong HoldInvoiceCltvExpiry = 40;
    private const int MaxEmptyBlocks = 10;

    /// <summary>The package must pay at least this share of the estimate (10 sat/vB), for rounding.</summary>
    private const decimal MinimumPackageRateSatPerVByte = 9m;

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public AnchorsCpfpTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("anchors-cpfp", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_CommitmentBelowTheFeeEstimate_When_WeForceClose_Then_OurAnchorChildPaysForItAndBothConfirmTogether()
    {
        // Arrange: an anchors channel whose commitment pays about 1 sat/vB
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, LightningMoney.Satoshis(300_000), ct,
                                                             AnchorsHarness.FloorFeeRatePerKw);
        var model = AnchorsHarness.GetModel(node, channel.ChannelId);

        // Act: force close
        var commitment = await _harness.ForceCloseAsync(node, channel, ct);
        var (ourAnchor, _) = AnchorsHarness.FindAnchors(model, commitment);
        var commitmentFee = (long)AnchorsHarness.Capacity.Satoshi - commitment.TotalOut.Satoshi;
        var commitmentRate = (decimal)commitmentFee / commitment.GetVirtualSize();
        Console.WriteLine($"Commitment {commitment.GetHash()}: fee {commitmentFee} sat, {commitmentRate:F2} sat/vB");
        Assert.True(commitmentRate < MinimumPackageRateSatPerVByte / 2, $"commitment at {commitmentRate} sat/vB");

        // Assert: a child spends our anchor and wallet coins, and pays for the package
        var anchorOutPoint = new OutPoint(commitment.GetHash(), ourAnchor);
        var child = await WaitForAnchorChildAsync(anchorOutPoint, ct);
        await AssertCpfpChildAsync(commitment, commitmentFee, anchorOutPoint, child, ct);

        // One block takes both (whatever child is current by then)
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [node], ct);
        var mined = await AssertMinedTogetherAsync(commitment, anchorOutPoint, ct);

        // The child's outputs are our wallet's; the funding spend is our local commitment
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();
        await Poll.UntilAsync(() => Enumerable.Range(0, mined.Outputs.Count)
                                              .Any(v => utxos.TryGetUtxo(mined.GetHash().ToBytes(), (uint)v, out _)),
                              AnchorsHarness.Timeout, "the child's change credited to our wallet", ct);
        var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
        await _harness.AssertLndClosedAsync(node, david, channel, commitment.GetHash(),
                                            ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ct);
    }

    [Fact]
    public async Task Given_CpfpChildLeftOutOfBlocks_When_BlocksPassBeforeTheHtlcDeadline_Then_ChildReplacedWithHigherFeeAndCommitmentConfirmedInTime()
    {
        // Arrange: an anchors channel at the floor feerate with our HTLC to david's hold invoice in it
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, null, ct, AnchorsHarness.FloorFeeRatePerKw);
        var model = AnchorsHarness.GetModel(node, channel.ChannelId);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [], ct,
                                                                   "o7 cpfp rbf", HoldInvoiceCltvExpiry);
        try
        {
            var inFlight = await node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
            Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
            await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          AnchorsHarness.Timeout, ct);
            var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(node, channel.ChannelId,
                                                                            HtlcDirection.Outgoing, ct);

            // Act: force close; the first child goes out
            var commitment = await _harness.ForceCloseAsync(node, channel, ct);
            var (ourAnchor, _) = AnchorsHarness.FindAnchors(model, commitment);
            var commitmentFee = (long)AnchorsHarness.Capacity.Satoshi - commitment.TotalOut.Satoshi;
            var anchorOutPoint = new OutPoint(commitment.GetHash(), ourAnchor);
            var first = await WaitForAnchorChildAsync(anchorOutPoint, ct);
            await AssertCpfpChildAsync(commitment, commitmentFee, anchorOutPoint, first, ct);
            var firstFee = await _harness.FeeAsync(first, ct);
            Console.WriteLine($"HTLC {htlc.Id}: cltv_expiry {htlc.CltvExpiry}; first child {first.GetHash()} fee "
                            + $"{firstFee}");

            // The miners leave the package out: blocks without it, one at a time, until the child is replaced
            Transaction? replacement = null;
            for (var i = 0; i < MaxEmptyBlocks && replacement is null; i++)
            {
                await _harness.MineEmptyBlocksAsync(1, node, [david], ct);
                replacement = await FindReplacementAsync(anchorOutPoint, first.GetHash(), ct);
            }

            // Assert: replaced with a higher fee, the first child gone, the package still paying for itself
            Assert.NotNull(replacement);
            var replacementFee = await _harness.FeeAsync(replacement, ct);
            Console.WriteLine($"Replacement {replacement.GetHash()} fee {replacementFee} (was {firstFee})");
            Assert.True(replacementFee > firstFee, $"replacement fee {replacementFee}, first {firstFee}");
            Assert.DoesNotContain(first.GetHash(), await _harness.Fixture.Bitcoin.GetRawMempoolAsync(ct));
            await AssertCpfpChildAsync(commitment, commitmentFee, anchorOutPoint, replacement, ct);

            // Blocks take it again: the commitment confirms before the HTLC's deadline
            await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [node], ct);
            await AssertMinedTogetherAsync(commitment, anchorOutPoint, ct);
            var info = await _harness.Fixture.Bitcoin.GetRawTransactionInfoAsync(commitment.GetHash(), ct);
            var confirmedAt = (uint)(await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
            Assert.True(confirmedAt < htlc.CltvExpiry, $"commitment at {confirmedAt}, expiry {htlc.CltvExpiry}");
        }
        finally
        {
            await AnchorsHarness.CancelHoldInvoiceQuietlyAsync(david, paymentHash);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The mempool spender of our anchor other than <paramref name="replaced"/>, looked for during a few seconds (the
    /// node bumps after it processed the block), or null.
    /// </summary>
    private async Task<Transaction?> FindReplacementAsync(OutPoint anchor, uint256 replaced, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var current = await _harness.FindMempoolSpenderAsync(anchor, ct);
            if (current is not null && current.GetHash() != replaced)
                return current;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return null;
    }

    private Task<Transaction> WaitForAnchorChildAsync(OutPoint anchor, CancellationToken ct) =>
        Poll.ForAsync(() => _harness.FindMempoolSpenderAsync(anchor, ct), AnchorsHarness.Timeout,
                      $"a child spending our anchor {anchor} in the mempool", ct);

    /// <summary>
    /// The child spends our anchor (a <c>&lt;sig&gt;</c> + anchor script witness) and at least one wallet input, and the
    /// package pays at least <see cref="MinimumPackageRateSatPerVByte"/>.
    /// </summary>
    private async Task AssertCpfpChildAsync(Transaction commitment, long commitmentFee, OutPoint anchor,
                                            Transaction child, CancellationToken ct)
    {
        var anchorInput = Assert.Single(child.Inputs, i => i.PrevOut == anchor);
        Assert.Equal(2, anchorInput.WitScript.PushCount);
        Assert.Equal(AnchorsHarness.SigHashAll, AnchorsHarness.SigHashOf(anchorInput.WitScript[0]));
        Assert.True(AnchorsHarness.HasForeignInput(child, commitment.GetHash()),
                    "the child spends no wallet input (330 sat cannot pay for the package)");
        Assert.All(child.Inputs.Where(i => i.PrevOut != anchor),
                   i => Assert.NotEqual(commitment.GetHash(), i.PrevOut.Hash));

        var childFee = (await _harness.FeeAsync(child, ct)).Satoshi;
        var packageRate = (decimal)(commitmentFee + childFee)
                        / (commitment.GetVirtualSize() + child.GetVirtualSize());
        Console.WriteLine($"Child {child.GetHash()}: {child.Inputs.Count} inputs, fee {childFee} sat, vsize "
                        + $"{child.GetVirtualSize()}; package {packageRate:F2} sat/vB");
        Assert.True(packageRate >= MinimumPackageRateSatPerVByte, $"package at {packageRate:F2} sat/vB");
    }

    /// <summary>The commitment and a child spending our anchor are in the same block; returns that child.</summary>
    private async Task<Transaction> AssertMinedTogetherAsync(Transaction commitment, OutPoint anchor,
                                                             CancellationToken ct)
    {
        var info = await _harness.Fixture.Bitcoin.GetRawTransactionInfoAsync(commitment.GetHash(), ct);
        Assert.True(info.Confirmations >= 1, "the commitment is not confirmed");
        var block = await _harness.Fixture.Bitcoin.GetBlockAsync(info.BlockHash, ct);
        var child = block.Transactions.SingleOrDefault(t => t.Inputs.Any(i => i.PrevOut == anchor));
        Assert.NotNull(child);
        Console.WriteLine($"Block {info.BlockHash}: commitment {commitment.GetHash()} and child {child.GetHash()}");
        return child;
    }
}