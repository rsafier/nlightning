using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Testing.Lnd.Lnrpc;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Application.Channels.Safety.Interfaces;
using Domain.Money;
using Domain.Onchain.Enums;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 B5-FAIL-06 with package relay (NL-380, O7-T4 gap (a)): our anchors commitment pays less than our bitcoind's
/// mempool minimum (a fee spike after the last <c>update_fee</c>), so bitcoind refuses it alone and its CPFP child is
/// an orphan; the node must send commitment and child together (<c>submitpackage</c>,
/// <c>IBitcoinChainService.SubmitPackageAsync</c>) and both confirm, against LND david.
/// </summary>
/// <remarks>
/// <para>Our node runs on a <see cref="RelayBitcoind"/> (the miner's image, a 5 MB mempool, synced from and relaying
/// to the miner; a pod in the network's namespace) whose mempool the test fills with 6 sat/vB transactions until
/// bitcoind trims it: its
/// <c>mempoolminfee</c> rises to about 7 sat/vB while <c>minrelaytxfee</c> stays at 1 sat/vB. The channel is opened at
/// our opener's lowest feerate (1,000 sat/kw, about 4 sat/vB), so the commitment is between the two: refused alone
/// ("mempool min fee not met"), accepted in a package whose feerate (the child pays for the 10 sat/vB estimate) is
/// above the minimum. A commitment below <c>minrelaytxfee</c> would be refused even in a package (Bitcoin Core 28+:
/// only TRUC transactions may be below it there; commitments are version 2), which is why the test raises the dynamic
/// minimum and not <c>-minrelaytxfee</c>.</para>
/// <para>The evidence is chain-side only: the commitment is in the relay's mempool although its own feerate is below
/// the minimum the relay had when it was broadcast (bitcoind only takes such a transaction in a package), a child
/// spending our anchor is there with it, and the relay was the only way to the miner for our commitment (nobody else
/// has it), which mines both in one block. Run with <c>scripts/run-cluster.sh -n 1 --suite anchors</c>.</para>
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsPackageRelayTests : IAsyncLifetime
{
    /// <summary>The fill transactions' feerate: above the ~4 sat/vB commitment, below the 10 sat/vB estimate.</summary>
    private const decimal FillRateSatPerVByte = 6m;

    /// <summary>Fill until the relay's minimum is above this (bitcoind sets it to the evicted rate + 1 sat/vB).</summary>
    private const decimal TargetMinFeeSatPerVByte = 6m;

    private const int MaxFills = 80;

    private readonly AnchorsHarness _harness;
    private RelayBitcoind? _relay;
    private NLightningTestNode? _node;

    public AnchorsPackageRelayTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _relay = await RelayBitcoind.StartAsync(_harness.Fixture, Money.Coins(1m), ct);
        _node = await _harness.CreateNodeAsync("anchors-package", ct, bitcoin: _relay.Endpoint);
    }

    [Fact]
    public async Task Given_CommitmentBelowTheMempoolMinimum_When_WeForceClose_Then_CommitmentAndChildEnterAsAPackageAndConfirmTogether()
    {
        // Arrange: an anchors channel whose commitment pays about 4 sat/vB; then a relay mempool whose minimum is above
        // that
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var relay = _relay!;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, LightningMoney.Satoshis(300_000), ct,
                                                             AnchorsHarness.LowFeeRatePerKw);
        var model = AnchorsHarness.GetModel(node, channel.ChannelId);
        var minRelayFee = await relay.GetMinRelayFeeSatPerVByteAsync(ct);
        var mempoolMinFee = await relay.FillMempoolAsync(FillRateSatPerVByte, TargetMinFeeSatPerVByte, MaxFills,
                                                         [david], [node], ct);

        // Act: force close through the fail-the-channel service (the only broadcaster of our commitment)
        var outcome = await node.Services.GetRequiredService<IChannelFailureService>()
                                .FailChannelAsync(channel.ChannelId,
                                                  new ChannelFailureRequest("O7 package relay proof force close",
                                                                            "force closing the channel"), ct);
        Assert.NotNull(outcome.CommitmentTxId);
        var commitmentTxId = new uint256((byte[])outcome.CommitmentTxId.Value);
        Console.WriteLine($"Force closed {channel.ChannelId}: {outcome.Status}, commitment {commitmentTxId}");

        // Assert: the commitment is in the relay's mempool although it could not enter alone
        await Poll.UntilAsync(() => relay.IsInMempoolAsync(commitmentTxId, ct), AnchorsHarness.Timeout,
                              "our commitment in the relay's mempool (as a package)", ct);
        var commitment = await relay.Rpc.GetRawTransactionAsync(commitmentTxId, true, ct);
        var commitmentFee = (long)AnchorsHarness.Capacity.Satoshi - commitment.TotalOut.Satoshi;
        var commitmentRate = (decimal)commitmentFee / commitment.GetVirtualSize();
        Console.WriteLine($"Commitment {commitmentTxId}: fee {commitmentFee} sat, {commitmentRate:F2} sat/vB; relay "
                        + $"minrelaytxfee {minRelayFee} sat/vB, mempoolminfee {mempoolMinFee} sat/vB at the close");
        Assert.True(commitmentRate >= minRelayFee,
                    $"commitment at {commitmentRate:F2} sat/vB is below minrelaytxfee: no package can carry it");
        Assert.True(commitmentRate < mempoolMinFee,
                    $"commitment at {commitmentRate:F2} sat/vB is not below the mempool minimum {mempoolMinFee}");

        // With a child that spends our anchor and wallet coins, the package above the minimum
        var (ourAnchor, _) = AnchorsHarness.FindAnchors(model, commitment);
        var anchorOutPoint = new OutPoint(commitmentTxId, ourAnchor);
        var child = await Poll.ForAsync(() => relay.FindMempoolSpenderAsync(anchorOutPoint, ct),
                                        AnchorsHarness.Timeout, "a child spending our anchor in the relay's mempool",
                                        ct);
        Assert.True(AnchorsHarness.HasForeignInput(child, commitmentTxId), "the child spends no wallet input");
        var childFee = (await InputValueAsync(relay, child, ct) - child.TotalOut).Satoshi;
        var packageRate = (decimal)(commitmentFee + childFee) / (commitment.GetVirtualSize() + child.GetVirtualSize());
        Console.WriteLine($"Child {child.GetHash()}: fee {childFee} sat, package {packageRate:F2} sat/vB");
        Assert.True(packageRate >= mempoolMinFee, $"package at {packageRate:F2} sat/vB, minimum {mempoolMinFee}");

        // The relay passes both on to the miner (wait for both, or a block could take the commitment alone), whose
        // next block takes them together
        await Poll.UntilAsync(async () =>
            (await _harness.Fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(commitmentTxId)
         && await _harness.FindMempoolSpenderAsync(anchorOutPoint, ct) is not null,
                              AnchorsHarness.Timeout, "the relay passed commitment and child on to the miner", ct);
        var info = await _harness.MineUntilConfirmedAsync(node, [david], commitmentTxId, ct);
        var block = await _harness.Fixture.Bitcoin.GetBlockAsync(info.BlockHash, ct);
        Assert.Contains(block.Transactions, t => t.Inputs.Any(i => i.PrevOut == anchorOutPoint));
        Console.WriteLine($"Block {info.BlockHash}: commitment {commitmentTxId} with its child");
        var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
        await _harness.AssertLndClosedAsync(node, david, channel, commitmentTxId,
                                            ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        if (_relay is not null)
            await _relay.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>The value a transaction spends, its prevouts read from the relay (txindex or mempool).</summary>
    private static async Task<Money> InputValueAsync(RelayBitcoind relay, Transaction tx, CancellationToken ct)
    {
        var total = Money.Zero;
        foreach (var input in tx.Inputs)
        {
            var parent = await relay.Rpc.GetRawTransactionAsync(input.PrevOut.Hash, true, ct);
            total += parent.Outputs[(int)input.PrevOut.N].Value;
        }

        return total;
    }
}