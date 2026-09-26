using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Cheater;
using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O5 on an anchors channel (O7-T4; breach): LND david restarts on an old <c>channel.db</c>
/// (<see cref="LndChannelDbRollback"/>) while our node is down and force-closes with a commitment it revoked. Our node
/// classifies the revoked commitment and takes every output that is not an anchor: david's <c>to_local</c> by
/// penalty and our CSV-1 <c>to_remote</c> (B5-REV-02/03; with anchors no output of the revoked commitment needs a
/// fee input). The 330-sat anchors are left alone (sweeping them costs more than they hold).
/// </summary>
/// <remarks>
/// The same breach as <c>OnchainO5Tests</c> (a) on an anchors channel. Needs lane O7-X3 (penalty and CSV-1 rules
/// for anchors channels). Run with <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsO5Tests : IAsyncLifetime
{
    private readonly AnchorsHarness _harness;

    public AnchorsO5Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Given_LndRestartsOnAnOldAnchorsChannelDb_When_ItForceCloses_Then_WeTakeEveryNonAnchorOutput()
    {
        // Arrange: us -> david with a push; payments both ways; david's channel.db copied; three more payments each way
        var ct = TestContext.Current.CancellationToken;
        var node = await _harness.CreateNodeAsync("anchors-o5-victim", ct);
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, LightningMoney.Satoshis(300_000), ct);
        var channelId = channel.ChannelId;
        var channelPoint = channel.ChannelPoint();
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(david, channelPoint, ct);
        Assert.NotNull(lndChannel);

        await AnchorsHarness.PayLndAsync(node, david, channelId, channelPoint, 20_000, ct);
        await AnchorsHarness.LndPaysUsAsync(node, david, lndChannel.ChanId, channelId, channelPoint, 30_000, ct);
        using var rollback = new LndChannelDbRollback(_harness.Fixture, "david");
        await rollback.TakeSnapshotAsync(ct);
        await WaitUsableAsync(node, david, channelId, ct);
        var snapshotState = await node.GetChannelAsync(channelId, ct);
        Console.WriteLine($"[o7-o5] snapshot state: {snapshotState.Describe()}");

        for (var i = 0; i < 3; i++)
        {
            await AnchorsHarness.PayLndAsync(node, david, channelId, channelPoint, 10_000, ct);
            await AnchorsHarness.LndPaysUsAsync(node, david, lndChannel.ChanId, channelId, channelPoint, 5_000, ct);
        }

        var now = await node.GetChannelAsync(channelId, ct);
        Assert.True(now.RemoteCommitmentNumber > snapshotState.RemoteCommitmentNumber + 1, now.Describe());
        var walletBefore = AnchorsHarness.WalletBalance(node);
        await node.StopAsync();

        // Act: david restarts on the old database and force-closes while we are down; one block; we start again
        await rollback.RestoreSnapshotAsync(ct);
        var revokedTxId = await ForceCloseWhenStartedAsync(david, channelPoint, ct);
        var revoked = await _harness.Fixture.Bitcoin.GetRawTransactionAsync(revokedTxId, true, ct);
        Console.WriteLine($"[o7-o5] david force-closed with {revokedTxId}");
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [], ct);
        await node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [david], [node], ct);

        // Assert: classified as revoked; both anchors present in it
        var close = await AnchorsHarness.WaitForCloseAsync(node, channelId, ct);
        Assert.Equal(ChannelCloseKind.RevokedCommitment, close.Kind);
        Assert.Equal((TxId)revokedTxId.ToBytes(), close.CommitmentTransactionId);
        var (ourAnchor, peerAnchor) = AnchorsHarness.FindAnchors(AnchorsHarness.GetModel(node, channelId), revoked);
        var takenVouts = Enumerable.Range(0, revoked.Outputs.Count).Select(v => (uint)v)
                                   .Where(v => v != ourAnchor && v != peerAnchor).ToList();
        Assert.NotEmpty(takenVouts);

        // Every non-anchor output is spent by a transaction of ours, all confirmed
        var ours = new Dictionary<uint256, Transaction>();
        await Poll.UntilAsync(async () =>
        {
            foreach (var row in await AnchorsHarness.GetRowsAsync(node, channelId))
            {
                if (row.ResolvingTransactionId is not { } txId)
                    continue;
                var hash = new uint256((byte[])txId);
                if (ours.ContainsKey(hash))
                    continue;
                try
                {
                    ours[hash] = await _harness.Fixture.Bitcoin.GetRawTransactionAsync(hash, true, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not in the mempool or a block (yet, or replaced)
                }
            }

            var spent = new HashSet<OutPoint>(ours.Values.SelectMany(t => t.Inputs.Select(i => i.PrevOut)));
            var allSpent = takenVouts.All(v => spent.Contains(new OutPoint(revokedTxId, v)));
            var confirmed = allSpent && await AllConfirmedAsync(ours.Values, revokedTxId, ct);
            if (!confirmed)
                await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [node], ct);
            return confirmed;
        }, AnchorsHarness.Timeout, "every non-anchor output of the revoked commitment taken and confirmed", ct);

        // The penalty spends david's to_local; our to_remote is swept with nSequence 1; the anchors are not ours
        var used = ours.Values.Where(t => t.Inputs.Any(i => i.PrevOut.Hash == revokedTxId)).ToList();
        var inputs = used.SelectMany(t => t.Inputs).Where(i => i.PrevOut.Hash == revokedTxId).ToList();
        Assert.DoesNotContain(inputs, i => i.PrevOut.N == ourAnchor || i.PrevOut.N == peerAnchor);
        var toRemoteSpend = Assert.Single(inputs, i => AnchorsHarness.IsAnchorsToRemoteSpend(i.WitScript));
        Assert.Equal(1u, toRemoteSpend.Sequence.Value);
        foreach (var tx in used)
            Console.WriteLine($"[o7-o5] {tx.GetHash()}: {tx.Inputs.Count} input(s), {tx.TotalOut.Satoshi} sat out");

        // The wallet gained what those transactions pay out less any wallet coins they spent
        var gained = 0L;
        foreach (var tx in used)
            gained += tx.TotalOut.Satoshi - (await ForeignInputValueAsync(tx, revokedTxId, ct)).Satoshi;
        var taken = takenVouts.Sum(v => revoked.Outputs[(int)v].Value.Satoshi);
        Console.WriteLine($"[o7-o5] taken outputs {taken} sat, gained {gained} sat");
        Assert.InRange(taken - gained, 1, (long)AnchorsHarness.Capacity.Satoshi / 100);
        await Poll.UntilAsync(() => (AnchorsHarness.WalletBalance(node) - walletBefore).Satoshi == gained,
                              AnchorsHarness.Timeout, $"the victim's wallet gained {gained} sat", ct);
        await Poll.ForAsync(async () =>
        {
            var channels = await david.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                          cancellationToken: ct);
            var found = channels.Channels.FirstOrDefault(c => c.ChannelPoint == channelPoint);
            if (found is null)
                await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [node], ct);
            return found;
        }, AnchorsHarness.Timeout, "david lists the channel closed", ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// LND answers GetInfo (synced) before its server is started and refuses CloseChannel until then: retries the force
    /// close; returns the commitment's txid.
    /// </summary>
    private static async Task<uint256> ForceCloseWhenStartedAsync(LNDNodeConnection david, string channelPoint,
                                                                  CancellationToken ct)
    {
        var parts = channelPoint.Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(AnchorsHarness.Timeout);
        PendingUpdate? pending = null;
        while (pending is null)
        {
            try
            {
                using var closeCall = david.LightningClient.CloseChannel(new CloseChannelRequest
                {
                    ChannelPoint = new ChannelPoint { FundingTxidStr = parts[0], OutputIndex = uint.Parse(parts[1]) },
                    Force = true
                }, cancellationToken: closeTimeout.Token);
                while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
                    pending = closeCall.ResponseStream.Current.ClosePending;
                Assert.NotNull(pending);
            }
            catch (RpcException e) when (e.Status.Detail.Contains("still in the process of starting"))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), closeTimeout.Token);
            }
        }

        return new uint256(pending.Txid.ToByteArray());
    }

    private async Task<bool> AllConfirmedAsync(IEnumerable<Transaction> txs, uint256 revokedTxId,
                                               CancellationToken ct)
    {
        foreach (var tx in txs.Where(t => t.Inputs.Any(i => i.PrevOut.Hash == revokedTxId)))
        {
            try
            {
                if ((await _harness.Fixture.Bitcoin.GetRawTransactionInfoAsync(tx.GetHash(), ct)).Confirmations == 0)
                    return false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false; // replaced
            }
        }

        return true;
    }

    /// <summary>The value of the inputs of <paramref name="tx"/> that do not spend the revoked commitment.</summary>
    private async Task<Money> ForeignInputValueAsync(Transaction tx, uint256 revokedTxId, CancellationToken ct)
    {
        var total = Money.Zero;
        foreach (var input in tx.Inputs.Where(i => i.PrevOut.Hash != revokedTxId))
        {
            var parent = await _harness.Fixture.Bitcoin.GetRawTransactionAsync(input.PrevOut.Hash, true, ct);
            total += parent.Outputs[(int)input.PrevOut.N].Value;
        }

        return total;
    }

    private async Task WaitUsableAsync(NLightningTestNode node, LNDNodeConnection david,
                                       Domain.Channels.ValueObjects.ChannelId channelId, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var usable = (await node.GetChannelAsync(channelId, ct)).IsUsable();
            var channels = await david.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                        cancellationToken: ct);
            usable &= channels.Channels.Any(c => c.Active && c.RemotePubkey == node.NodeIdHex);
            if (!usable)
                await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [node], ct);
            return usable;
        }, AnchorsHarness.Timeout, $"channel {channelId} usable", ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [david], [node], ct);
    }
}