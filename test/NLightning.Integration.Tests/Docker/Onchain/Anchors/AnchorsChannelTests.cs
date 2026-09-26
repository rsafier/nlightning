using Lnrpc;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Domain.Money;
using Domain.Onchain.Enums;
using Fixtures;
using Utils;

/// <summary>
/// The ground the O7-T4 anchors proofs stand on, against LND david: an <c>option_anchors</c>
/// (<c>option_anchors_zero_fee_htlc_tx</c>) channel we fund is negotiated on both ends, carries HTLCs both ways (every
/// HTLC signature we send is <c>SIGHASH_SINGLE|ANYONECANPAY</c> and LND accepts it, LND's are checked by our signer),
/// and our force close broadcasts a BOLT 3 anchors commitment: both 330-sat anchors, the P2WSH CSV-1
/// <c>to_remote</c>, recorded with an <see cref="OutputDescriptorKind.OurAnchor"/> row and a
/// <see cref="OutputDescriptorKind.DelayedToLocal"/> row only (the peer's outputs get none).
/// </summary>
/// <remarks>
/// Needs only what exists before the O7 lanes (anchors commitments and HTLC signatures, BOLT 3 Appendix F); the
/// other anchors proofs need them. Run with <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsCategory)]
public class AnchorsChannelTests : IAsyncLifetime
{
    public const string AnchorsCategory = "Onchain.Anchors";

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public AnchorsChannelTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("anchors", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_AnchorsEnabled_When_WeOpenToLnd_Then_AnchorsChannelCarriesPaymentsBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _harness.Fixture.GetLndNode("david");

        // Act: open (the harness asserts the anchors channel type on both ends), then pay both ways
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, LightningMoney.Satoshis(300_000), ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(david, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        await AnchorsHarness.PayLndAsync(node, david, channel.ChannelId, channel.ChannelPoint(), 20_000, ct);
        await AnchorsHarness.LndPaysUsAsync(node, david, lndChannel.ChanId, channel.ChannelId,
                                            channel.ChannelPoint(), 10_000, ct);

        // Assert: both ends agree on the balances, the channel is still active and anchors in LND
        var ours = await node.GetChannelAsync(channel.ChannelId, ct);
        var theirs = await LndTestHelpers.GetChannelByPointAsync(david, channel.ChannelPoint(), ct);
        Assert.NotNull(theirs);
        Assert.True(ours.IsUsable(), ours.Describe());
        Assert.True(theirs.Active);
        Assert.Equal(CommitmentType.Anchors, theirs.CommitmentType);
        Assert.Equal(theirs.LocalBalance, ours.RemoteBalance.Satoshi);
        Console.WriteLine($"After the payments: {ours.Describe()}; LND local {theirs.LocalBalance}, "
                        + $"commit fee {theirs.CommitFee}");
    }

    [Fact]
    public async Task Given_AnchorsChannel_When_WeForceClose_Then_CommitmentHasBothAnchorsAndCsvOneToRemote()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, LightningMoney.Satoshis(300_000), ct);
        var model = AnchorsHarness.GetModel(node, channel.ChannelId);

        // Act: our commitment goes out
        var commitment = await _harness.ForceCloseAsync(node, channel, ct);

        // Assert: BOLT 3 anchors commitment: our and david's 330-sat anchors, to_local, and to_remote as P2WSH
        var (ourAnchor, peerAnchor) = AnchorsHarness.FindAnchors(model, commitment);
        Console.WriteLine($"Commitment {commitment.GetHash()}: {commitment.Outputs.Count} outputs, our anchor "
                        + $"{ourAnchor}, david's anchor {peerAnchor}, vsize {commitment.GetVirtualSize()}, fee "
                        + $"{(long)AnchorsHarness.Capacity.Satoshi - commitment.TotalOut.Satoshi} sat");
        Assert.Equal(4, commitment.Outputs.Count);
        await LogMempoolSpendersAsync(commitment, ct);
        Assert.All(commitment.Outputs, o => Assert.True(o.ScriptPubKey.IsScriptType(ScriptType.P2WSH),
                                                        $"output {o.ScriptPubKey} is not P2WSH"));
        Assert.Equal(0x20u, commitment.LockTime.Value >> 24);
        Assert.Equal(0x80u, commitment.Inputs[0].Sequence.Value >> 24);

        // The commitment confirms; the funding spend is our local commitment with a row per output
        var confirmed = await _harness.MineUntilConfirmedAsync(node, [david], commitment.GetHash(), ct);
        Assert.True(confirmed.Confirmations >= 1);
        var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
        // Rows only for our outputs (our anchor and to_local): the peer's anchor and balance are not ours to resolve
        var rows = await Poll.ForAsync(async () =>
        {
            var found = (await AnchorsHarness.GetRowsAsync(node, channel.ChannelId))
                       .Where(r => r.TransactionId == close.CommitmentTransactionId).ToList();
            return found.Count >= 2 ? found : null;
        }, AnchorsHarness.Timeout, "the rows of our outputs of the commitment", ct);
        var byVout = rows.ToDictionary(r => r.OutputIndex, r => r.Descriptor);
        Console.WriteLine($"Rows: {string.Join(", ", byVout.Select(r => $"{r.Key}={r.Value}"))}");
        Assert.Equal(2, byVout.Count);
        Assert.Equal(OutputDescriptorKind.OurAnchor, byVout[ourAnchor]);
        Assert.DoesNotContain(peerAnchor, byVout.Keys);
        Assert.Single(byVout.Values, d => d == OutputDescriptorKind.DelayedToLocal);
        await _harness.AssertLndClosedAsync(node, david, channel, commitment.GetHash(),
                                            ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ct);
    }

    /// <summary>What spends the unconfirmed commitment in the mempool (e.g. a CPFP child of either side), for the log.</summary>
    private async Task LogMempoolSpendersAsync(Transaction commitment, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        for (var vout = 0; vout < commitment.Outputs.Count; vout++)
        {
            var spender = await _harness.FindMempoolSpenderAsync(new OutPoint(commitment.GetHash(), vout), ct);
            if (spender is not null)
                Console.WriteLine($"Output {vout} ({commitment.Outputs[vout].Value}) spent in the mempool by "
                                + $"{spender.GetHash()} ({spender.Inputs.Count} inputs)");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }
}