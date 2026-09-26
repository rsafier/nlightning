using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Application.Channels.Safety.Interfaces;
using Cheater;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O6 (fees, rebroadcast, reorgs), against bitcoind and LND david:
/// <list type="bullet">
///   <item>(a) the block holding our <c>to_local</c> sweep is invalidated and a competing empty block wins (with the
///   sweep also gone from bitcoind's mempool): the chain monitor rolls the confirmation back, the executor unresolves
///   the output, we send the stored sweep again and it confirms in the next block (O6-T3).</item>
///   <item>(b) a penalty is unconfirmed when the victim stops and bitcoind's mempool forgets it: at startup the victim
///   sends it again from its <c>BroadcastTransactions</c> row, before any new block, and it confirms (O0-T1, O6).</item>
///   <item>(c) our <c>to_remote</c> sweep of david's commitment is kept out of blocks (<c>prioritisetransaction</c>
///   with a negative delta: the regtest way of a fee the miners do not take): every
///   <c>SweepConfTarget</c> blocks the <c>SweepScheduler</c> replaces it with a higher fee (BIP 125), twice, until a
///   replacement confirms (O6-T1).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>bitcoind has no RPC that drops one transaction from its mempool; the tests evict it by expiry: with
/// <c>setmocktime</c> 15 days ahead, the next accepted transaction makes bitcoind expire everything older than
/// <c>-mempoolexpiry</c> (336 hours), then the mock time is reset.</para>
/// <para>Run with <c>scripts/run-onchain.sh</c> (own process, own fixture, one framework).</para>
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainO6Tests : IAsyncLifetime
{
    private const long FundingSat = 1_000_000;
    private const long PushSat = 200_000;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly SweepFeePolicyOptions s_feePolicy = new();

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public OnchainO6Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Proof O6 (a).</summary>
    [Fact]
    public async Task Given_OurToLocalSweepBlockInvalidated_When_ACompetingEmptyBlockWins_Then_RebroadcastAndConfirmedAgain()
    {
        // Arrange: our commitment on chain and our to_local swept after the CSV
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("o6a", ct);
        var david = _fixture.GetLndNode("david");
        var channel = await OpenUsableChannelAsync(node, david, ct);
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channel.ChannelId, out var model));
        var csv = model.ChannelParams.Remote.ToSelfDelay;
        var (commitmentTxId, commitmentHeight) = await ForceCloseAndConfirmAsync(node, david, channel, ct);
        var toLocal = await Poll.ForAsync(async () => (await GetRowsAsync(node, channel.ChannelId))
                                                     .FirstOrDefault(r => r.TransactionId == commitmentTxId
                                                                       && r.Descriptor
                                                                       == OutputDescriptorKind.DelayedToLocal),
                                          s_timeout, "to_local row", ct);
        await MineToAsync(node, [david], commitmentHeight + csv - 1, ct);
        var sweepTxId = await WaitForResolvingTxAsync(node, channel.ChannelId, commitmentTxId, toLocal.OutputIndex, ct);
        await WaitInMempoolAsync(sweepTxId, ct);
        var sweepBlockHeight = await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
        var sweepBlock = await _fixture.Bitcoin.GetBlockHashAsync((int)sweepBlockHeight, ct);
        await Poll.UntilAsync(() => IsResolvedAtAsync(node, channel.ChannelId, commitmentTxId, toLocal.OutputIndex,
                                                      sweepBlockHeight), s_timeout, "to_local resolved by our sweep",
                              ct);
        Assert.Equal(BroadcastState.Confirmed, (await GetBroadcastAsync(node, sweepTxId)).State);

        // Act: the sweep's block is invalidated, bitcoind forgets the sweep, a competing empty block wins
        await _fixture.Bitcoin.InvalidateBlockAsync(sweepBlock, ct);
        await EvictMempoolAsync(ct);
        Assert.DoesNotContain(new uint256((byte[])sweepTxId), await _fixture.Bitcoin.GetRawMempoolAsync(ct));
        var address = await _fixture.Bitcoin.GetNewAddressAsync(ct);
        await _fixture.Bitcoin.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        Assert.Equal(sweepBlockHeight, (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct));
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [node], ct);

        // Assert: rolled back and sent again by us (bitcoind had forgotten it); the channel still resolving
        await Poll.UntilAsync(async () =>
        {
            var row = await GetRowAsync(node, channel.ChannelId, commitmentTxId, toLocal.OutputIndex);
            return (await GetBroadcastAsync(node, sweepTxId)).State == BroadcastState.Pending
                && row?.State == OutputResolutionState.Broadcast && row.ResolvedHeight is null;
        }, s_timeout, "the sweep's confirmation and the output's resolution rolled back", ct);
        await WaitInMempoolAsync(sweepTxId, ct);
        Assert.Equal(ChannelState.OnchainResolving, (await node.GetChannelAsync(channel.ChannelId, ct)).State);

        // Act: the next block
        var reconfirmedAt = await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);

        // Assert: confirmed again, one block higher, and the output resolved from that block
        await Poll.UntilAsync(() => IsResolvedAtAsync(node, channel.ChannelId, commitmentTxId, toLocal.OutputIndex,
                                                      reconfirmedAt), s_timeout,
                              "to_local resolved again from the new block", ct);
        var broadcast = await GetBroadcastAsync(node, sweepTxId);
        Assert.Equal(BroadcastState.Confirmed, broadcast.State);
        Assert.Equal(reconfirmedAt, broadcast.ConfirmedHeight);
        Console.WriteLine($"[o6a] sweep {new uint256((byte[])sweepTxId)} first at {sweepBlockHeight}, again at "
                        + $"{reconfirmedAt}");
    }

    /// <summary>Proof O6 (b).</summary>
    [Fact]
    public async Task Given_PenaltyUnconfirmed_When_RestartedWithTheMempoolCleared_Then_RebroadcastFromItsStoredRow()
    {
        // Arrange: the O5 (b) breach; the victim's penalties are in the mempool
        var ct = TestContext.Current.CancellationToken;
        var (victim, channelId, captured) = await PrepareCheaterBreachAsync(ct);
        await _fixture.Bitcoin.SendRawTransactionAsync(captured.Transaction, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [victim], ct);
        var penalties = await Poll.ForAsync(async () =>
        {
            using var scope = victim.Services.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                                  .GetByChannelIdAsync(channelId);
            var pending = rows.Where(b => b is { Purpose: BroadcastPurpose.Penalty, State: BroadcastState.Pending })
                              .Select(b => new uint256((byte[])b.TransactionId))
                              .ToList();
            var mempool = await _fixture.Bitcoin.GetRawMempoolAsync(ct);
            return pending.Count > 0 && pending.All(mempool.Contains) ? pending : null;
        }, s_timeout, "the victim's penalties in the mempool", ct);
        Console.WriteLine($"[o6b] penalties {string.Join(", ", penalties)}");

        // Act: the victim stops, bitcoind forgets the penalties, the victim starts again
        await victim.StopAsync();
        await EvictMempoolAsync(ct);
        var mempoolWithout = await _fixture.Bitcoin.GetRawMempoolAsync(ct);
        Assert.All(penalties, p => Assert.DoesNotContain(p, mempoolWithout));
        var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        await victim.StartAsync(ct);

        // Assert: sent again at startup, before any new block
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.GetRawMempoolAsync(ct);
            return penalties.All(mempool.Contains);
        }, s_timeout, "the penalties sent again from their stored rows", ct);
        Assert.Equal(tip, (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct));

        // Act / Assert: they confirm
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [victim], ct);
        foreach (var penalty in penalties)
        {
            var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(penalty, ct);
            Assert.True(info.Confirmations > 0, $"penalty {penalty} confirmed");
            Assert.All(info.Transaction.Inputs, i => Assert.Equal(captured.Transaction.GetHash(), i.PrevOut.Hash));
        }

        await Poll.UntilAsync(async () =>
        {
            using var scope = victim.Services.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                                  .GetByChannelIdAsync(channelId);
            return penalties.All(p => rows.Any(b => new uint256((byte[])b.TransactionId) == p
                                                 && b.State == BroadcastState.Confirmed));
        }, s_timeout, "the penalties recorded as confirmed", ct);
    }

    /// <summary>Proof O6 (c).</summary>
    [Fact]
    public async Task Given_LowFeeSweepNotMined_When_UnconfirmedForTheSweepTarget_Then_RbfBumpedUntilConfirmed()
    {
        // Arrange: david force-closes; we sweep our to_remote at once
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("o6c", ct);
        var david = _fixture.GetLndNode("david");
        var channel = await OpenUsableChannelAsync(node, david, ct);
        var commitmentTxId = await DavidForceClosesAsync(node, david, channel, ct);
        var toRemote = await Poll.ForAsync(async () => (await GetRowsAsync(node, channel.ChannelId))
                                                      .FirstOrDefault(r => r.TransactionId == commitmentTxId
                                                                        && r.Descriptor
                                                                        == OutputDescriptorKind.PaymentToRemote
                                                                        && r.ResolvingTransactionId is not null),
                                           s_timeout, "our to_remote sweep", ct);
        var amount = OutputDescriptorData.TryDecode(toRemote)!.AmountSat;
        var sweeps = new List<TxId> { toRemote.ResolvingTransactionId!.Value };
        var walletBefore = WalletBalance(node);

        // Act / Assert: twice, the current sweep is kept out of blocks until the scheduler replaces it
        for (var bump = 1; bump <= 2; bump++)
        {
            var current = sweeps[^1];
            await WaitInMempoolAsync(current, ct);
            await _fixture.Bitcoin.SendCommandAsync("prioritisetransaction", ct, new uint256((byte[])current).ToString(),
                                                    0, -100_000_000);
            await ChainSync.MineAndWaitAsync(_fixture, (int)s_feePolicy.SweepConfTarget, [david], [node], ct);
            var replacement = await Poll.ForAsync(async () =>
            {
                using var scope = node.Services.CreateScope();
                var rows = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                                      .BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);
                return rows.FirstOrDefault(b => b.ReplacesTransactionId == current);
            }, s_timeout, $"replacement {bump} of the sweep", ct);

            // The replaced sweep has left bitcoind's mempool: read it from our row
            var old = Transaction.Load((await GetBroadcastAsync(node, current)).RawTransaction, Network.RegTest);
            var @new = Transaction.Load(replacement.RawTransaction, Network.RegTest);
            var oldFee = (long)amount - old.Outputs.Sum(o => o.Value.Satoshi);
            var newFee = (long)amount - @new.Outputs.Sum(o => o.Value.Satoshi);
            Console.WriteLine($"[o6c] bump {bump}: {new uint256((byte[])current)} fee {oldFee} -> "
                            + $"{@new.GetHash()} fee {newFee} ({replacement.FeeratePerKw} sat/kw)");
            Assert.Equal(old.Inputs.Select(i => i.PrevOut), @new.Inputs.Select(i => i.PrevOut));
            Assert.True(newFee >= (long)new SweepFeePolicy().GetReplacementFee((ulong)oldFee, @new.GetVirtualSize()),
                        $"replacement fee {newFee} must outbid {oldFee} (BIP 125)");
            Assert.Equal(BroadcastState.Replaced, (await GetBroadcastAsync(node, current)).State);
            Assert.Equal(replacement.TransactionId,
                         (await GetRowAsync(node, channel.ChannelId, commitmentTxId, toRemote.OutputIndex))!
                        .ResolvingTransactionId);
            sweeps.Add(replacement.TransactionId);
        }

        // Act: the last replacement is left alone
        await WaitInMempoolAsync(sweeps[^1], ct);
        var minedAt = await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);

        // Assert: the last replacement confirmed and resolved to_remote; the wallet gained its output
        var final = await _fixture.Bitcoin.GetRawTransactionInfoAsync(new uint256((byte[])sweeps[^1]), ct);
        Assert.True(final.Confirmations > 0);
        await Poll.UntilAsync(() => IsResolvedAtAsync(node, channel.ChannelId, commitmentTxId, toRemote.OutputIndex,
                                                      minedAt), s_timeout,
                              "to_remote resolved by the last replacement", ct);
        var gained = final.Transaction.Outputs.Sum(o => o.Value.Satoshi);
        await Poll.UntilAsync(() => Task.FromResult((WalletBalance(node) - walletBefore).Satoshi == gained),
                              s_timeout, $"the wallet gained {gained} sat", ct);
        Assert.Equal(3, sweeps.Distinct().Count());
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["david"]);

        // A failed test must not leave the mock time set
        await _fixture.Bitcoin.SendCommandAsync("setmocktime", CancellationToken.None, 0);

        foreach (var node in _nodes)
            await node.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Evicts every transaction from bitcoind's mempool by expiry (see the class remarks): mock time 15 days ahead,
    /// one new wallet transaction (whose acceptance runs the expiry), mock time reset.
    /// </summary>
    private async Task EvictMempoolAsync(CancellationToken ct)
    {
        var bitcoin = _fixture.Bitcoin;
        var future = DateTimeOffset.UtcNow.AddDays(15).ToUnixTimeSeconds();
        await bitcoin.SendCommandAsync("setmocktime", ct, future);
        try
        {
            await bitcoin.SendToAddressAsync(await bitcoin.GetNewAddressAsync(ct), Money.Coins(0.0001m),
                                             cancellationToken: ct);
        }
        finally
        {
            await bitcoin.SendCommandAsync("setmocktime", ct, 0);
        }
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name);
        _nodes.Add(node);
        await node.StartAsync(ct);
        return node;
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(NLightningTestNode node,
                                                                                    LNDNodeConnection peer,
                                                                                    CancellationToken ct)
    {
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var peerAddress = await node.ConnectToAsync(peer, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                               LightningMoney.Satoshis(FundingSat))
        {
            PushAmount = LightningMoney.Satoshis(PushSat),
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [node], ct);
        return channel;
    }

    /// <summary>We fail the channel (our latest commitment broadcast), mine it, wait for the local-commitment close.
    /// </summary>
    private async Task<(TxId TxId, uint Height)> ForceCloseAndConfirmAsync(NLightningTestNode node,
                                                                           LNDNodeConnection peer,
                                                                           OpenChannelClientSubscriptionResponse channel,
                                                                           CancellationToken ct)
    {
        var outcome = await node.Services.GetRequiredService<IChannelFailureService>()
                                .FailChannelAsync(channel.ChannelId,
                                                  new ChannelFailureRequest("O6 proof force close",
                                                                            "force closing the channel"), ct);
        Assert.NotNull(outcome.CommitmentTxId);
        await WaitInMempoolAsync(outcome.CommitmentTxId.Value, ct);
        var height = await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
        await Poll.UntilAsync(() => IsClosedByAsync(node, channel.ChannelId, ChannelCloseKind.LocalCommitment,
                                                    outcome.CommitmentTxId.Value), s_timeout,
                              "the funding spend classified as our local commitment", ct);
        return (outcome.CommitmentTxId.Value, height);
    }

    /// <summary>david force-closes; its commitment confirms and we record the remote close. Returns its txid.</summary>
    private async Task<TxId> DavidForceClosesAsync(NLightningTestNode node, LNDNodeConnection david,
                                                   OpenChannelClientSubscriptionResponse channel, CancellationToken ct)
    {
        var parts = channel.ChannelPoint().Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        using var closeCall = david.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint
            {
                FundingTxidStr = parts[0],
                OutputIndex = uint.Parse(parts[1])
            },
            Force = true
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);

        // LND's ClosePending txid is in internal byte order, like ours
        var commitment = new TxId(pending.Txid.ToByteArray());
        await WaitInMempoolAsync(commitment, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
        await Poll.UntilAsync(() => IsClosedByAsync(node, channel.ChannelId, ChannelCloseKind.RemoteCommitment,
                                                    commitment), s_timeout, "david's commitment classified", ct);
        return commitment;
    }

    /// <summary>
    /// The breach of Proof O5 (b) up to the broadcast: cheater → victim, 1,000,000 sat with 100,000 pushed; one payment,
    /// the cheater's commitment k captured, three more payments (k revoked), the cheater stopped.
    /// </summary>
    private async Task<(NLightningTestNode Victim, ChannelId ChannelId, CapturedCommitment Captured)>
        PrepareCheaterBreachAsync(CancellationToken ct)
    {
        var cheater = await CreateNodeAsync("o6b-cheater", ct);
        var victim = await CreateNodeAsync("o6b-victim", ct);
        await cheater.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [cheater, victim], ct);
        await cheater.ConnectToAsync(victim, ct);
        var opened = await cheater.OpenChannelAsync(new OpenChannelClientRequest(victim.Address,
                                                                                 LightningMoney.Satoshis(FundingSat))
        {
            PushAmount = LightningMoney.Satoshis(100_000),
            FeeRatePerKw = LightningMoney.Satoshis(2_500)
        }, ct);
        var channelId = opened.ChannelId;
        await Poll.UntilAsync(async () =>
        {
            var usable = (await cheater.GetChannelAsync(channelId, ct)).IsUsable()
                      && (await victim.GetChannelAsync(channelId, ct)).IsUsable();
            if (!usable)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [cheater, victim], ct);
            return usable;
        }, s_timeout, $"channel {channelId} usable", ct);

        await PayAsync(cheater, victim, channelId, 20_000, ct);
        var captured = await StaleCommitmentCapture.CaptureAsync(cheater, channelId);
        for (var i = 0; i < 3; i++)
            await PayAsync(cheater, victim, channelId, 50_000, ct);

        await cheater.StopAsync();
        return (victim, channelId, captured);
    }

    private static async Task PayAsync(NLightningTestNode payer, NLightningTestNode payee, ChannelId channelId,
                                       long amountSat, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), "o6 payment", ct);
        var payment = await payer.PayInvoiceAsync(invoice.Bolt11, ct);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await Poll.UntilAsync(async () =>
        {
            var ours = await payer.GetChannelAsync(channelId, ct);
            var theirs = await payee.GetChannelAsync(channelId, ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && theirs is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && ours.LocalBalance == theirs.RemoteBalance
                && ours.LocalCommitmentNumber == theirs.RemoteCommitmentNumber
                && ours.RemoteCommitmentNumber == theirs.LocalCommitmentNumber;
        }, s_timeout, "the payment settled on both sides", ct);
    }

    private async Task MineToAsync(NLightningTestNode node, LNDNodeConnection[] peers, uint height,
                                   CancellationToken ct)
    {
        var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        if (height > tip)
            await ChainSync.MineAndWaitAsync(_fixture, (int)(height - tip), peers, [node], ct);
    }

    private async Task WaitInMempoolAsync(TxId txId, CancellationToken ct)
    {
        var displayTxId = new uint256((byte[])txId);
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(displayTxId),
                              s_timeout, $"{displayTxId} in the mempool", ct);
    }

    private static async Task<TxId> WaitForResolvingTxAsync(NLightningTestNode node, ChannelId channelId, TxId txId,
                                                            uint vout, CancellationToken ct)
    {
        TxId? found = null;
        await Poll.UntilAsync(async () =>
                                  (found = (await GetRowAsync(node, channelId, txId, vout))?.ResolvingTransactionId)
                                  is not null, s_timeout, $"a resolving transaction for {vout}", ct);
        return found!.Value;
    }

    private static async Task<IReadOnlyList<OutputResolutionModel>> GetRowsAsync(NLightningTestNode node,
                                                                                 ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetOutputsByChannelIdAsync(channelId);
    }

    private static async Task<OutputResolutionModel?> GetRowAsync(NLightningTestNode node, ChannelId channelId,
                                                                  TxId txId, uint vout) =>
        (await GetRowsAsync(node, channelId)).FirstOrDefault(r => r.TransactionId == txId && r.OutputIndex == vout);

    private static async Task<bool> IsResolvedAtAsync(NLightningTestNode node, ChannelId channelId, TxId txId,
                                                      uint vout, uint height)
    {
        var row = await GetRowAsync(node, channelId, txId, vout);
        return row is { State: OutputResolutionState.Resolved } && row.ResolvedHeight == height;
    }

    private static async Task<bool> IsClosedByAsync(NLightningTestNode node, ChannelId channelId,
                                                    ChannelCloseKind kind, TxId commitmentTxId)
    {
        using var scope = node.Services.CreateScope();
        var close = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                               .GetCloseAsync(channelId);
        return close is not null && close.Kind == kind && close.CommitmentTransactionId == commitmentTxId;
    }

    private static async Task<BroadcastTransactionModel> GetBroadcastAsync(NLightningTestNode node, TxId txId)
    {
        using var scope = node.Services.CreateScope();
        var broadcast = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                                   .GetByTransactionIdAsync(txId);
        Assert.NotNull(broadcast);
        return broadcast;
    }

    private static LightningMoney WalletBalance(NLightningTestNode node)
    {
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var height = node.BlockchainMonitor.LastProcessedBlockHeight;
        return utxos.GetConfirmedBalance(height) + utxos.GetUnconfirmedBalance(height);
    }
}