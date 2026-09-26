using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Application.Channels.Safety.Interfaces;
using Cheater;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan O8 (NL-098), the mempool reaction against real bitcoind and LND:
/// <list type="bullet">
///   <item>(a) alice → us → david, the downstream channel force closed by us while david holds the forwarded HTLC; david
///   settles and claims our offered HTLC on chain with the preimage: we read the preimage from the <b>unconfirmed</b>
///   claim and alice's payment succeeds before any block holds the claim.</item>
///   <item>(b) the O5 (b) breach, the revoked commitment sent to bitcoind and not mined: the victim broadcasts its
///   penalty while the commitment is still in the mempool, and one block confirms both.</item>
/// </list>
/// </summary>
/// <remarks>Run with <c>scripts/run-onchain.sh</c> (own process, own fixture, one framework).</remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainMempoolTests : IAsyncLifetime
{
    private const ulong HoldInvoiceCltvExpiry = 24;
    private const ushort OurCltvExpiryDelta = 40;
    private const uint OurFeeBaseMsat = 1_000;
    private const uint OurFeeProportionalMillionths = 100;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_feeRate = LightningMoney.Satoshis(2_500);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public OnchainMempoolTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Given_ForwardedHtlc_When_DownstreamClaimsWithThePreimageInTheMempool_Then_UpstreamFulfilledBeforeAnyBlockHoldsTheClaim()
    {
        // Arrange: alice -> us -> david (david's hold invoice, reachable only through our private channel)
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("o8a", ct, o =>
        {
            o.Routing.CltvExpiryDelta = OurCltvExpiryDelta;
            o.Routing.FeeBaseMsat = OurFeeBaseMsat;
            o.Routing.FeeProportionalMillionths = OurFeeProportionalMillionths;
        });
        var alice = _fixture.GetLndNode("alice");
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        var upstream = await OpenUsableChannelAsync(node, alice, LightningMoney.Satoshis(500_000), ct);
        var downstream = await OpenUsableChannelAsync(node, david, null, ct);
        var upstreamLnd = await LndTestHelpers.GetChannelByPointAsync(alice, upstream.ChannelPoint(), ct);
        Assert.NotNull(upstreamLnd);
        var downstreamScid = (await node.GetChannelAsync(downstream.ChannelId, ct)).ShortChannelId;
        Assert.NotNull(downstreamScid);
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(node.NodeIdHex, downstreamScid.Value.ToUInt64(),
                                                                   OurFeeBaseMsat, OurFeeProportionalMillionths,
                                                                   OurCltvExpiryDelta));
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [hint], ct,
                                                                   "o8 a claimed from the mempool",
                                                                   HoldInvoiceCltvExpiry);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var payment = LndTestHelpers.SendPaymentV2Async(
            alice, LndTestHelpers.PinnedPayment(holdInvoice.PaymentRequest, [upstreamLnd.ChanId]), ct,
            TimeSpan.FromMinutes(10));
        await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        await WaitForHtlcInBothCommitmentsAsync(node, upstream.ChannelId, HtlcDirection.Incoming, ct);
        var outgoing = await WaitForHtlcInBothCommitmentsAsync(node, downstream.ChannelId, HtlcDirection.Outgoing, ct);

        // Act: we force close the downstream channel (our commitment confirms), then david settles: LND claims our
        // offered HTLC with the preimage. Its sweeper publishes on a new block, so blocks are mined only until the
        // claim reaches the mempool, never after
        var commitment = await ForceCloseAndConfirmAsync(node, david, downstream, ct);
        var htlcVout = await PollValueAsync(async () => (await GetRowsAsync(node, downstream.ChannelId))
                                                     .FirstOrDefault(r => r.TransactionId == commitment
                                                                       && r.Descriptor
                                                                       == OutputDescriptorKind.LocalOfferedHtlc)
                                                    ?.OutputIndex, "the offered HTLC's row", ct);
        await LndTestHelpers.SettleInvoiceAsync(david, preimage, ct);
        var htlcOutpoint = new OutPoint(new uint256((byte[])commitment), htlcVout);
        var (claim, claimSeenAt) = await WaitForMempoolSpendAsync(htlcOutpoint, [alice, david], [node], ct);
        Console.WriteLine($"[o8a] david's claim {claim.GetHash()} in the mempool at tip {claimSeenAt}");

        // Assert: alice's payment succeeded while the claim is still unconfirmed and no block was mined
        Assert.True(await WaitUntilCompletedAsync(payment, s_timeout, ct), "alice's payment never completed");
        var result = await payment;
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, result.Status);
        Assert.Equal(Convert.ToHexString(preimage).ToLowerInvariant(), result.PaymentPreimage);
        Assert.Equal(claimSeenAt, (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct));
        Assert.Contains(claim.GetHash(), await _fixture.Bitcoin.GetRawMempoolAsync(ct));
        Assert.Contains(node.NodeLog, l => l.Contains("seen in unconfirmed transaction", StringComparison.Ordinal));
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(downstream.ChannelId, out var channel));
        Assert.Equal(preimage, (byte[])channel.Commitments!.GetHtlc(HtlcDirection.Outgoing, outgoing.Id)!
                                                   .KnownPreimage!.Value);
        Assert.True((await node.GetChannelAsync(upstream.ChannelId, ct)).IsUsable());
    }

    [Fact]
    public async Task Given_RevokedCommitmentInTheMempool_When_Seen_Then_PenaltyBroadcastBeforeItConfirmsAndBothConfirmTogether()
    {
        // Arrange: the O5 (b) breach up to the broadcast
        var ct = TestContext.Current.CancellationToken;
        var (victim, channelId, captured) = await PrepareCheaterBreachAsync(ct);
        var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        var revokedHash = captured.Transaction.GetHash();

        // Act: the cheater broadcasts k; no block is mined
        await _fixture.Bitcoin.SendRawTransactionAsync(captured.Transaction, ct);
        Console.WriteLine($"[o8b] revoked commitment {revokedHash} sent at tip {tip}");
        var penalty = await Poll.ForAsync(async () => await FindMempoolSpenderAsync(revokedHash, ct), s_timeout,
                                          "our penalty in the mempool behind the revoked commitment", ct);

        // Assert (before any block): every output of k spent by the penalty, stored as a pending penalty, and nothing
        // recorded as a close (the mempool is never a confirmation)
        Assert.Equal(tip, (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct));
        var spent = penalty.Inputs.Where(i => i.PrevOut.Hash == revokedHash).Select(i => i.PrevOut.N).Order();
        Assert.Equal(Enumerable.Range(0, captured.Transaction.Outputs.Count).Select(i => (uint)i), spent);
        TxId penaltyTxId = penalty.GetHash().ToBytes();
        var stored = await GetBroadcastAsync(victim, penaltyTxId);
        Assert.NotNull(stored);
        Assert.Equal(BroadcastPurpose.Penalty, stored.Purpose);
        Assert.Null(await GetCloseAsync(victim, channelId));

        // Act: one block
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [victim], ct);

        // Assert: the commitment and the penalty confirmed in that block; the victim recorded the revoked close
        // with every row resolved by the penalty it had already broadcast (nothing built twice)
        var commitmentInfo = await _fixture.Bitcoin.GetRawTransactionInfoAsync(revokedHash, ct);
        var penaltyInfo = await _fixture.Bitcoin.GetRawTransactionInfoAsync(penalty.GetHash(), ct);
        Console.WriteLine($"[o8b] commitment in {commitmentInfo.BlockHash}, penalty in {penaltyInfo.BlockHash}");
        Assert.Equal(1u, commitmentInfo.Confirmations);
        Assert.Equal(1u, penaltyInfo.Confirmations);
        var close = await Poll.ForAsync(() => GetCloseAsync(victim, channelId), s_timeout,
                                        "the victim recorded the funding spend", ct);
        Assert.Equal(ChannelCloseKind.RevokedCommitment, close.Kind);
        await Poll.UntilAsync(async () =>
        {
            var rows = await GetRowsAsync(victim, channelId);
            return rows.Count == captured.Transaction.Outputs.Count
                && rows.All(r => r.ResolvingTransactionId == penaltyTxId);
        }, s_timeout, "every row named the penalty prepared from the mempool", ct);
        var penalties = (await GetBroadcastsAsync(victim, channelId))
                       .Where(b => b.Purpose == BroadcastPurpose.Penalty)
                       .ToList();
        Assert.Equal([penaltyTxId], penalties.Select(p => p.TransactionId));
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);
        }

        foreach (var node in _nodes)
            await node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct,
                                                           Action<Domain.Node.Options.NodeOptions>? configure = null)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, configureNodeOptions: configure);
        _nodes.Add(node);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>
    /// Mines one block at a time (with every node at the tip after each) until a mempool transaction spends
    /// <paramref name="outpoint"/>, and returns it with the tip at which it was found. Nothing is mined once it is
    /// there.
    /// </summary>
    private async Task<(Transaction Spender, uint Tip)> WaitForMempoolSpendAsync(
        OutPoint outpoint, LNDNodeConnection[] peers, NLightningTestNode[] nodes, CancellationToken ct)
    {
        for (var block = 0; block < 20; block++)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var txId in await _fixture.Bitcoin.GetRawMempoolAsync(ct))
                {
                    var transaction = await TryGetMempoolTransactionAsync(txId, ct);
                    if (transaction?.Inputs.Any(i => i.PrevOut == outpoint) == true)
                        return (transaction, (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct));
                }

                await Task.Delay(500, ct);
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, peers, nodes, ct);
        }

        throw new TimeoutException($"No mempool transaction spent {outpoint}");
    }

    private async Task<Transaction?> FindMempoolSpenderAsync(uint256 parent, CancellationToken ct)
    {
        foreach (var txId in await _fixture.Bitcoin.GetRawMempoolAsync(ct))
        {
            var transaction = await TryGetMempoolTransactionAsync(txId, ct);
            if (transaction?.Inputs.Any(i => i.PrevOut.Hash == parent) == true)
                return transaction;
        }

        return null;
    }

    private async Task<Transaction?> TryGetMempoolTransactionAsync(uint256 txId, CancellationToken ct)
    {
        try
        {
            return await _fixture.Bitcoin.GetRawTransactionAsync(txId, true, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null; // Mined or evicted meanwhile
        }
    }

    /// <summary>
    /// The breach of Proof O5 (b) up to the broadcast: cheater → victim, 1,000,000 sat with 100,000 pushed; one
    /// payment, the cheater's commitment k captured, three more payments (the cheater revokes k), the cheater stopped.
    /// </summary>
    private async Task<(NLightningTestNode Victim, ChannelId ChannelId, CapturedCommitment Captured)>
        PrepareCheaterBreachAsync(CancellationToken ct)
    {
        var cheater = await CreateNodeAsync("o8cheater", ct);
        var victim = await CreateNodeAsync("o8victim", ct);
        await cheater.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [cheater, victim], ct);
        await cheater.ConnectToAsync(victim, ct);
        var opened = await cheater.OpenChannelAsync(new OpenChannelClientRequest(victim.Address, s_capacity)
        {
            PushAmount = LightningMoney.Satoshis(100_000),
            FeeRatePerKw = s_feeRate
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
        await ChainSync.WaitAllAtTipAsync(_fixture, [cheater, victim], ct);

        await PayAsync(cheater, victim, channelId, 20_000, ct);
        var captured = await StaleCommitmentCapture.CaptureAsync(cheater, channelId);
        for (var i = 0; i < 3; i++)
            await PayAsync(cheater, victim, channelId, 50_000, ct);
        var now = await victim.GetChannelAsync(channelId, ct);
        Assert.True(now.RemoteCommitmentNumber > captured.Number + 1,
                    $"the cheater's commitment {captured.Number} is not revoked yet ({now.Describe()})");

        await cheater.StopAsync();
        return (victim, channelId, captured);
    }

    /// <summary><paramref name="payer"/> pays <paramref name="payee"/>'s invoice over their channel, and both sides
    /// settle.</summary>
    private static async Task PayAsync(NLightningTestNode payer, NLightningTestNode payee, ChannelId channelId,
                                       long amountSat, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), "o8 payment", ct);
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

    /// <summary>
    /// Fails the channel through <see cref="IChannelFailureService"/> (our latest commitment, broadcast), mines it and
    /// waits until the node recorded the funding spend as our local commitment.
    /// </summary>
    private async Task<TxId> ForceCloseAndConfirmAsync(NLightningTestNode node, LNDNodeConnection peer,
                                                       OpenChannelClientSubscriptionResponse channel,
                                                       CancellationToken ct)
    {
        var outcome = await node.Services.GetRequiredService<IChannelFailureService>()
                                .FailChannelAsync(channel.ChannelId,
                                                  new ChannelFailureRequest("O8 proof force close",
                                                                            "force closing the channel"), ct);
        Assert.NotNull(outcome.CommitmentTxId);
        var displayTxId = new uint256((byte[])outcome.CommitmentTxId.Value);
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(displayTxId),
                              s_timeout, "our commitment in the mempool", ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
        var commitmentTxId = outcome.CommitmentTxId.Value;
        await Poll.UntilAsync(async () =>
        {
            var close = await GetCloseAsync(node, channel.ChannelId);
            return close?.Kind == ChannelCloseKind.LocalCommitment && close.CommitmentTransactionId == commitmentTxId;
        }, s_timeout, "the funding spend classified as our local commitment", ct);
        return outcome.CommitmentTxId.Value;
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(NLightningTestNode node,
        LNDNodeConnection peer, LightningMoney? push, CancellationToken ct)
    {
        var peerAddress = await node.ConnectToAsync(peer, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                               LightningMoney.Satoshis(1_000_000))
        {
            PushAmount = push,
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

    private static Task<HtlcRecord> WaitForHtlcInBothCommitmentsAsync(NLightningTestNode node, ChannelId channelId,
                                                                      HtlcDirection direction, CancellationToken ct) =>
        Poll.ForAsync(() =>
        {
            var model = node.ChannelMemoryRepository.TryGetChannel(channelId, out var c) ? c : null;
            return model?.Commitments?.Htlcs.Values.FirstOrDefault(h => h.Direction == direction
                                                                    && h.IsInCommit(CommitmentSide.Local)
                                                                    && h.IsInCommit(CommitmentSide.Remote));
        }, s_timeout, $"{direction} HTLC in both commitments of {channelId}", ct);

    private static async Task<ChannelCloseModel?> GetCloseAsync(NLightningTestNode node, ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetCloseAsync(channelId);
    }

    private static async Task<IReadOnlyList<OutputResolutionModel>> GetRowsAsync(NLightningTestNode node,
                                                                                 ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetOutputsByChannelIdAsync(channelId);
    }

    private static async Task<BroadcastTransactionModel?> GetBroadcastAsync(NLightningTestNode node, TxId txId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                          .GetByTransactionIdAsync(txId);
    }

    private static async Task<IReadOnlyList<BroadcastTransactionModel>> GetBroadcastsAsync(NLightningTestNode node,
        ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                          .GetByChannelIdAsync(channelId);
    }

    /// <summary><see cref="Poll.ForAsync{T}(Func{Task{T}}, TimeSpan, string, CancellationToken, TimeSpan?)"/> for a
    /// value type.</summary>
    private static async Task<T> PollValueAsync<T>(Func<Task<T?>> probe, string description, CancellationToken ct)
        where T : struct
    {
        T? found = null;
        await Poll.UntilAsync(async () => (found = await probe()) is not null, s_timeout, description, ct);
        return found!.Value;
    }

    private static async Task<bool> WaitUntilCompletedAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        await Task.WhenAny(task, Task.Delay(timeout, ct));
        return task.IsCompleted;
    }
}