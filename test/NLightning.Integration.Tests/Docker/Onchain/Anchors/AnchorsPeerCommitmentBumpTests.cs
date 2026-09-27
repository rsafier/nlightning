using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Application.Channels.Safety;
using Domain.Channels.Enums;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Protocol.Messages;
using Fixtures;
using Infrastructure.Transport.Interfaces;
using Utils;

/// <summary>
/// BOLT 5 on the peer's anchors commitment (NL-381, O7-T4 gap (b)): LND david force-closes an anchors channel whose
/// commitment pays our opener's lowest feerate (1,000 sat/kw, about 4 sat/vB, below the 10 sat/vB estimate) and
/// carries an HTLC david offered us whose preimage we hold, so we have a deadline (its <c>cltv_expiry</c>) and only the
/// peer's fee to meet it. We bump david's commitment ourselves: a child spends <b>our</b> anchor on it
/// (<c>to_remote_anchor</c> from david's side, the anchor keyed by our funding key) plus wallet inputs, the commitment
/// confirms through that child, and we then claim the HTLC with the preimage before its expiry.
/// </summary>
/// <remarks>
/// <para>Setup as Proof O4 (c) on anchors (<see cref="AnchorsO4Tests"/>): david pays our invoice, our fulfill is saved
/// but the wire dies before it goes out (<see cref="CrashableTcpService"/>; the node keeps running, so it sees
/// david's commitment in its mempool, but no connection comes back, so the fulfill never reaches david).</para>
/// <para>So that nothing but our child can pay for the commitment, every output of david's wallet is leased first
/// (<c>walletrpc.LeaseOutput</c>): LND cannot build its own anchor CPFP. The miners leave the commitment out (empty
/// blocks through <c>generateblock</c>) until our child is there, for as long as the HTLC's fulfillment deadline
/// allows: the node may bump as soon as it sees the commitment or only as the deadline approaches. The loop stops
/// two blocks before <c>FulfillDeadline(cltv_expiry)</c> (<c>cltv_expiry - 18</c> by default,
/// <see cref="ChannelSafetyOptions"/>): from that height our own <c>HtlcExpiryMonitor</c> fails the channel and
/// broadcasts our commitment, which conflicts with david's, so the empty block and the package's block both stay
/// below it. Before
/// the block that takes the package the commitment's own fee is removed from bitcoind's block template
/// (<c>prioritisetransaction</c>, as <see cref="AnchorsCpfpTests"/> does), as is that of any other spender of david's
/// anchor, so the commitment can confirm only through our child. Run with
/// <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.</para>
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsPeerCommitmentBumpTests : IAsyncLifetime
{
    /// <summary>
    /// Blocks the empty-block loop keeps free before the HTLC's fulfillment deadline: the next empty block and the
    /// package's block.
    /// </summary>
    private const uint FulfillDeadlineMargin = 2;

    /// <summary>The package must pay at least this share of the estimate (10 sat/vB), for rounding.</summary>
    private const decimal MinimumPackageRateSatPerVByte = 9m;

    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public AnchorsPeerCommitmentBumpTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("anchors-peer-bump", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_DavidsLowFeeCommitmentWithAnHtlcWeCanClaim_When_DavidForceCloses_Then_OurAnchorChildGetsItConfirmedBeforeTheDeadline()
    {
        // Arrange: a low-feerate anchors channel; david pays our invoice and our fulfill is saved but lost
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(Node, david, s_push, ct, AnchorsHarness.LowFeeRatePerKw);
        var model = AnchorsHarness.GetModel(Node, channel.ChannelId);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(david, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        var tcpService = Assert.IsType<CrashableTcpService>(Node.Services.GetRequiredService<ITcpService>());
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the fulfill's save: cut every connection before it goes out
            if (args.ResponseMessage is not UpdateFulfillHtlcMessage || crashed.Task.IsCompleted)
                return;

            tcpService.CrashAsync().GetAwaiter().GetResult();
            crashed.TrySetResult();
        };
        var invoice = await Node.CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "o7 peer commitment bump", ct);
        await AnchorsHarness.PayUntilSentAsync(david, invoice.Bolt11!, lndChannel.ChanId, crashed.Task, ct);
        var htlc = Assert.Single(AnchorsHarness.GetHtlcs(Node, channel.ChannelId),
                                 h => h.Direction == HtlcDirection.Incoming);
        Assert.NotNull(htlc.Removal);
        Assert.True(htlc.Removal.IsFulfill);
        Console.WriteLine($"Fulfill of HTLC {htlc.Id} saved and lost; cltv_expiry {htlc.CltvExpiry}");

        var leaseId = RandomUtils.GetBytes(32);
        var leased = await AnchorsHarness.LeaseLndWalletAsync(david, leaseId, TimeSpan.FromMinutes(30), ct);
        try
        {
            // Act: david force-closes; its commitment (about 4 sat/vB) is in the mempool
            var commitmentTxId = await AnchorsHarness.LndForceCloseAsync(david, channel, ct);
            var commitment = await _harness.WaitInMempoolAsync(commitmentTxId.ToBytes(), ct);
            var commitmentFee = (long)AnchorsHarness.Capacity.Satoshi - commitment.TotalOut.Satoshi;
            var commitmentRate = (decimal)commitmentFee / commitment.GetVirtualSize();
            Console.WriteLine($"David's commitment {commitmentTxId}: fee {commitmentFee} sat, {commitmentRate:F2} sat/vB");
            Assert.True(commitmentRate < MinimumPackageRateSatPerVByte, $"commitment at {commitmentRate} sat/vB");
            var (ourAnchor, davidsAnchor) = AnchorsHarness.FindAnchors(model, commitment);
            var ourAnchorOutPoint = new OutPoint(commitmentTxId, ourAnchor);

            // The miners leave it out until our child is there, bounded by the HTLC's fulfillment deadline (our
            // monitor fails the channel there) rather than a fixed block count, so a node that bumps only as the
            // deadline approaches passes too
            var fulfillDeadline = Node.Services.GetRequiredService<IOptions<ChannelSafetyOptions>>().Value
                                      .CreatePolicy(null).FulfillDeadline(htlc.CltvExpiry);
            Console.WriteLine($"Fulfillment deadline {fulfillDeadline} (cltv_expiry {htlc.CltvExpiry})");
            var child = await FindChildAsync(ourAnchorOutPoint);
            while (child is null)
            {
                var tip = (uint)await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct);
                if (tip + FulfillDeadlineMargin >= fulfillDeadline)
                    break;

                await _harness.MineEmptyBlocksAsync(1, Node, [david], ct);
                child = await FindChildAsync(ourAnchorOutPoint);
            }

            // Assert: our child spends our anchor on david's commitment with a SIGHASH_ALL signature and wallet coins,
            // and pays for the package
            Assert.NotNull(child);
            var anchorInput = Assert.Single(child.Inputs, i => i.PrevOut == ourAnchorOutPoint);
            Assert.Equal(2, anchorInput.WitScript.PushCount);
            Assert.Equal(AnchorsHarness.SigHashAll, AnchorsHarness.SigHashOf(anchorInput.WitScript[0]));
            Assert.True(AnchorsHarness.HasForeignInput(child, commitmentTxId),
                        "the child spends no wallet input (330 sat cannot pay for the package)");
            var childFee = (await _harness.FeeAsync(child, ct)).Satoshi;
            var packageRate = (decimal)(commitmentFee + childFee)
                            / (commitment.GetVirtualSize() + child.GetVirtualSize());
            Console.WriteLine($"Our child {child.GetHash()}: fee {childFee} sat, package {packageRate:F2} sat/vB");
            Assert.True(packageRate >= MinimumPackageRateSatPerVByte, $"package at {packageRate:F2} sat/vB");

            // One block takes the commitment, only through our child
            await DeprioritiseAsync(commitmentTxId, commitmentFee, ct);
            if (await _harness.FindMempoolSpenderAsync(new OutPoint(commitmentTxId, davidsAnchor), ct) is { } lndChild)
                await DeprioritiseAsync(lndChild.GetHash(), (await _harness.FeeAsync(lndChild, ct)).Satoshi, ct);
            await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [Node], ct);
            var info = await _harness.Fixture.Bitcoin.GetRawTransactionInfoAsync(commitmentTxId, ct);
            Assert.True(info.Confirmations >= 1, "david's commitment is not confirmed");
            var block = await _harness.Fixture.Bitcoin.GetBlockAsync(info.BlockHash, ct);
            var mined = block.Transactions.SingleOrDefault(t => t.Inputs.Any(i => i.PrevOut == ourAnchorOutPoint));
            Assert.NotNull(mined);
            var confirmedAt = (uint)(await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
            Console.WriteLine($"Block {info.BlockHash} ({confirmedAt}): commitment with our child {mined.GetHash()}");
            Assert.True(confirmedAt < htlc.CltvExpiry, $"commitment at {confirmedAt}, expiry {htlc.CltvExpiry}");

            // The funding spend is david's commitment, and we claim the HTLC with the preimage before its expiry
            var close = await AnchorsHarness.WaitForCloseAsync(Node, channel.ChannelId, ct);
            Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
            Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
            var row = await AnchorsHarness.WaitForRowAsync(Node, channel.ChannelId,
                                                           o => o is
                                                           {
                                                               Descriptor: OutputDescriptorKind.RemoteOfferedHtlc,
                                                               ResolvingTransactionId: not null
                                                           }, "our preimage claim saved", ct);
            Assert.Equal(htlc.Id, row.HtlcId);
            var claimInfo = await _harness.MineUntilConfirmedAsync(Node, [david], row.ResolvingTransactionId!.Value,
                                                                   ct);
            var claimedAt = (uint)(await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct) - claimInfo.Confirmations
                                 + 1);
            Assert.True(claimedAt < htlc.CltvExpiry, $"claimed at {claimedAt}, expiry {htlc.CltvExpiry}");
            var claimInput = Assert.Single(claimInfo.Transaction.Inputs,
                                           i => i.PrevOut == new OutPoint(commitmentTxId, row.OutputIndex));
            Assert.Equal((byte[])htlc.Removal.PaymentPreimage!.Value, claimInput.WitScript[1]);
        }
        finally
        {
            await AnchorsHarness.ReleaseLndWalletAsync(david, leaseId, leased);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The mempool spender of our anchor, looked for during a few seconds (the node reacts to the mempool and to
    /// blocks), or null.
    /// </summary>
    private async Task<Transaction?> FindChildAsync(OutPoint anchor)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await _harness.FindMempoolSpenderAsync(anchor, ct) is { } child)
                return child;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return null;
    }

    /// <summary>
    /// Removes <paramref name="fee"/> from a mempool transaction's fee in bitcoind's block template only
    /// (<c>prioritisetransaction</c>), so it is mined only as the ancestor of a child that pays for it.
    /// </summary>
    private async Task DeprioritiseAsync(uint256 txId, long fee, CancellationToken ct)
    {
        await _harness.Fixture.Bitcoin.SendCommandAsync("prioritisetransaction", ct, txId.ToString(), 0, -fee);
        Console.WriteLine($"{txId} deprioritised by {fee} sat");
    }
}