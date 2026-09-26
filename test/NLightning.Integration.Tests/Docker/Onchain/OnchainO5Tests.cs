using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Revoked;
using Cheater;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O5 (breach): a peer broadcasts a commitment it revoked and our node takes every output with
/// penalty (justice) transactions (<c>RevokedCommitResolver</c>, run by the on-chain resolution executor).
/// </summary>
/// <remarks>
/// <para>(b) is the deterministic proof: two NLightning nodes, the cheater's stale commitment signed by a test-only
/// copy of its signer (<see cref="StaleCommitmentCapture"/>) and sent with <c>sendrawtransaction</c> after the channel
/// moved three states on. (a) is the interop proof: LND david restarts on an old <c>channel.db</c>
/// (<see cref="LndChannelDbRollback"/>) while our node is down and force-closes, broadcasting its revoked commitment.</para>
/// <para>Both need the on-chain channel watcher and executor of lane W5-A (funding spend → <c>ChannelCloses</c> row of
/// kind <see cref="ChannelCloseKind.RevokedCommitment"/> → resolver rounds → broadcasts). Run with
/// <c>scripts/run-onchain.sh</c> (own process, own fixture).</para>
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainO5Tests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_feeRate = LightningMoney.Satoshis(2_500);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public OnchainO5Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Proof O5 (b): the cheater (funder) signs its local commitment k while it still holds most of the channel, pays
    /// the victim three more times (revoking k), goes offline and broadcasts k. The victim classifies the spend as
    /// revoked, penalizes every output and its wallet gains the whole channel less the two transaction fees.
    /// </summary>
    [Fact]
    public async Task Given_NLightningCheaterBroadcastsRevokedCommitment_When_Confirmed_Then_VictimPenalizesEveryOutput()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (victim, channelId, captured, walletBefore) = await PrepareCheaterBreachAsync(ct);

        // Act: the cheater broadcasts k
        var revokedTxId = await _fixture.Bitcoin.SendRawTransactionAsync(captured.Transaction, ct);
        Console.WriteLine($"[o5b] revoked commitment {revokedTxId} sent");
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [victim], ct);

        // Assert
        await AssertPenalizedAsync(victim, channelId, captured.Transaction, walletBefore, ct);
    }

    /// <summary>
    /// Lane proof of the resolver alone (no on-chain watcher or executor in the node yet, lane W5-D): the same breach
    /// as (b), with <see cref="RevokedCommitResolver"/> built from the victim's own services (database, shachain,
    /// chain, wallet, signer) and driven round by round by the test, which does the executor's part: rows kept in
    /// memory, broadcasts saved and published through the node's <see cref="IChainBroadcaster"/>. bitcoind accepts and
    /// mines the penalty, and the victim's wallet gains the channel. Explicit: once the executor is wired it runs the
    /// resolver itself, and the end-to-end proof above covers the whole path.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_NLightningCheaterBreach_When_ResolverDrivenByTheTest_Then_BitcoindMinesThePenalty()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (victim, channelId, captured, walletBefore) = await PrepareCheaterBreachAsync(ct);
        await _fixture.Bitcoin.SendRawTransactionAsync(captured.Transaction, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [victim], ct);

        // Act / Assert
        await DriveResolverUntilPenaltyMinedAsync(victim, channelId, captured.Transaction, walletBefore, ct);
    }

    /// <summary>
    /// Proof O5 (a) with the resolver driven by the test (see
    /// <see cref="Given_NLightningCheaterBreach_When_ResolverDrivenByTheTest_Then_BitcoindMinesThePenalty"/>): LND's
    /// real revoked commitment, our shachain and revocation log, a penalty bitcoind accepts and mines.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_LndBreach_When_ResolverDrivenByTheTest_Then_BitcoindMinesThePenalty()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (node, _, _, _, revoked, walletBefore) = await PrepareLndBreachAsync(ct);
        var channelId = (await node.ListChannelsAsync(ct)).Channels.Single().ChannelId;

        // Act / Assert
        await DriveResolverUntilPenaltyMinedAsync(node, channelId, revoked, walletBefore, ct);
    }

    /// <summary>
    /// Plays the on-chain executor for <see cref="RevokedCommitResolver"/> built from <paramref name="victim"/>'s own
    /// services: one round per block (rows kept in memory, broadcasts saved and published through the node's
    /// <see cref="IChainBroadcaster"/>) until the penalty is mined; then checks it spends every output of the revoked
    /// commitment and the wallet gained its output.
    /// </summary>
    private async Task DriveResolverUntilPenaltyMinedAsync(NLightningTestNode victim, ChannelId channelId,
                                                           Transaction revoked, LightningMoney walletBefore,
                                                           CancellationToken ct)
    {
        var revokedTxId = revoked.GetHash();
        var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(revokedTxId, ct);
        var spentAt = (uint)(await _fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
        Assert.True(victim.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        var number = channel!.CommitmentNumber!.Decode(revoked.LockTime.Value, revoked.Inputs[0].Sequence.Value);
        Assert.NotNull(number);
        Console.WriteLine($"[o5-lane] revoked commitment {revokedTxId} (number {number}) at {spentAt}");
        var close = new ChannelCloseModel(channelId, ChannelCloseKind.RevokedCommitment, revokedTxId.ToBytes(),
                                          number, spentAt, info.BlockHash.ToBytes(), DateTimeOffset.UtcNow);
        var dataSource = ActivatorUtilities.CreateInstance<RevokedCommitDataSource>(victim.Services);
        var resolver = ActivatorUtilities.CreateInstance<RevokedCommitResolver>(victim.Services,
                                                                                 (IRevokedCommitDataSource)dataSource);
        var broadcaster = victim.Services.GetRequiredService<IChainBroadcaster>();
        var rows = new List<OutputResolutionModel>();

        var penalties = new List<TxId>();
        for (var round = 0; round < 6 && penalties.Count == 0; round++)
        {
            var actions = await resolver.ResolveAsync(close, rows, victim.BlockchainMonitor.LastProcessedBlockHeight,
                                                      ct);
            foreach (var action in actions)
            {
                switch (action)
                {
                    case UpsertOutputAction upsert:
                        rows.RemoveAll(r => r.TransactionId == upsert.Output.TransactionId
                                         && r.OutputIndex == upsert.Output.OutputIndex);
                        rows.Add(upsert.Output);
                        break;
                    case BroadcastAction broadcast:
                        Assert.True(await broadcaster.SaveAndPublishAsync(broadcast.Transaction),
                                    $"bitcoind refused penalty {broadcast.Transaction.TransactionId}");
                        penalties.Add(broadcast.Transaction.TransactionId);
                        break;
                    case AlertAction alert:
                        Console.WriteLine($"[o5-lane] alert {alert.RequirementId}: {alert.Message}");
                        break;
                }
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, [], [victim], ct);
        }

        // One batched penalty spending every output of the revoked commitment, mined, credited to the wallet
        var penaltyTxId = Assert.Single(penalties);
        var penalty = await _fixture.Bitcoin.GetRawTransactionInfoAsync(new uint256((byte[])penaltyTxId), ct);
        Assert.True(penalty.Confirmations > 0);
        Assert.Equal(revoked.Outputs.Count(o => o.Value.Satoshi > 330), penalty.Transaction.Inputs.Count);
        Assert.All(penalty.Transaction.Inputs, i => Assert.Equal(revokedTxId, i.PrevOut.Hash));
        var gained = penalty.Transaction.Outputs.Sum(o => o.Value.Satoshi);
        var commitmentFee = (long)s_capacity.Satoshi - revoked.Outputs.Sum(o => o.Value.Satoshi);
        Console.WriteLine($"[o5-lane] penalty {penalty.TransactionId}: {penalty.Transaction.Inputs.Count} inputs, "
                        + $"gained {gained} sat, commitment fee {commitmentFee} sat");
        Assert.InRange((long)s_capacity.Satoshi - commitmentFee - gained, 1, (long)s_capacity.Satoshi / 100);
        await Poll.UntilAsync(() => Task.FromResult((WalletBalance(victim) - walletBefore).Satoshi == gained),
                              s_timeout, $"the victim's wallet gained {gained} sat", ct);
    }

    /// <summary>
    /// The breach of Proof O5 (b) up to the broadcast: cheater → victim, 1,000,000 sat with 100,000 pushed; one
    /// payment, the cheater's commitment k captured (<see cref="StaleCommitmentCapture"/>), three more payments (the
    /// cheater revokes k with its own signer), the cheater stopped.
    /// </summary>
    private async Task<(NLightningTestNode Victim, ChannelId ChannelId, CapturedCommitment Captured,
                        LightningMoney WalletBefore)> PrepareCheaterBreachAsync(CancellationToken ct)
    {
        var cheater = await CreateNodeAsync("cheater", ct);
        var victim = await CreateNodeAsync("victim", ct);
        await cheater.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [cheater, victim], ct);
        await cheater.ConnectToAsync(victim, ct);
        var opened = await cheater.OpenChannelAsync(new OpenChannelClientRequest(victim.Address, s_capacity)
        {
            PushAmount = LightningMoney.Satoshis(100_000),
            FeeRatePerKw = s_feeRate
        }, ct);
        var channelId = opened.ChannelId;
        await WaitUsableAsync([], [cheater, victim], channelId, ct);

        await PayAsync(cheater, victim, channelId, 20_000, ct);
        var captured = await StaleCommitmentCapture.CaptureAsync(cheater, channelId);
        var capturedChannel = await cheater.GetChannelAsync(channelId, ct);
        Console.WriteLine($"[o5b] state k = {captured.Number}: {capturedChannel.Describe()}");

        for (var i = 0; i < 3; i++)
            await PayAsync(cheater, victim, channelId, 50_000, ct);
        var now = await victim.GetChannelAsync(channelId, ct);
        Console.WriteLine($"[o5b] now (victim's view): {now.Describe()}");
        Assert.True(now.RemoteCommitmentNumber > captured.Number + 1,
                    $"the cheater's commitment {captured.Number} is not revoked yet ({now.Describe()})");

        await cheater.StopAsync();
        return (victim, channelId, captured, WalletBalance(victim));
    }

    /// <summary>
    /// Proof O5 (a): LND david cheats by database rollback. We open to david with a push, pay both ways, david's
    /// <c>channel.db</c> is copied while it is stopped, then three payments each way revoke that state; our node stops,
    /// david restarts on the copy and force-closes (it cannot know it is outdated: our node is down and sends no
    /// <c>channel_reestablish</c>). One block later our node starts, classifies the revoked commitment and penalizes it.
    /// </summary>
    [Fact]
    public async Task Given_LndRestartsOnAnOldChannelDb_When_ItForceCloses_Then_WePenalizeTheRevokedCommitment()
    {
        // Arrange / Act: david restarts on the old database and force-closes while we are down; we start again
        var ct = TestContext.Current.CancellationToken;
        var (node, david, channelId, channelPoint, revoked, walletBefore) = await PrepareLndBreachAsync(ct);

        // Assert
        await AssertPenalizedAsync(node, channelId, revoked, walletBefore, ct);
        var closed = await Poll.ForAsync(async () =>
        {
            var channels = await david.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                          cancellationToken: ct);
            var found = channels.Channels.FirstOrDefault(c => c.ChannelPoint == channelPoint);
            if (found is null)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
            return found;
        }, s_timeout, "david lists the channel closed", ct);
        Console.WriteLine($"[o5a] LND close type {closed.CloseType}, settled {closed.SettledBalance}");
    }

    /// <summary>
    /// The breach of Proof O5 (a) up to our restart: us → david, 1,000,000 sat with 300,000 pushed; a payment each way;
    /// david's <c>channel.db</c> copied (container stopped, <c>/home/lnd/.lnd/data/graph/regtest/channel.db</c>);
    /// three payments each way revoke that state; our node stops; david restarts on the copy and force-closes (its
    /// revoked commitment is mined); our node starts.
    /// </summary>
    private async Task<(NLightningTestNode Node, LNDNodeConnection David, ChannelId ChannelId, string ChannelPoint,
                        Transaction Revoked, LightningMoney WalletBefore)> PrepareLndBreachAsync(CancellationToken ct)
    {
        var node = await CreateNodeAsync("breach-victim", ct);
        var david = _fixture.GetLndNode("david");
        Console.WriteLine($"[o5a] {await LndTestHelpers.GetVersionAsync(david, ct)}");
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        var davidAddress = await node.ConnectToAsync(david, ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(davidAddress, s_capacity)
        {
            PushAmount = LightningMoney.Satoshis(300_000),
            FeeRatePerKw = s_feeRate
        }, ct);
        var channelId = opened.ChannelId;
        var channelPoint = opened.ChannelPoint();
        await WaitUsableAsync([david], [node], channelId, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(david, channelPoint, ct);
        Assert.NotNull(lndChannel);

        await PayLndAsync(node, david, channelId, channelPoint, 20_000, ct);
        await LndPaysUsAsync(node, david, lndChannel.ChanId, channelId, channelPoint, 30_000, ct);
        using var rollback = new LndChannelDbRollback(_fixture, "david");
        await rollback.TakeSnapshotAsync(ct);
        Console.WriteLine($"[o5a] david's channel.db is {rollback.DatabasePath}");
        await WaitUsableAsync([david], [node], channelId, ct);
        var snapshotState = await node.GetChannelAsync(channelId, ct);
        Console.WriteLine($"[o5a] snapshot state: {snapshotState.Describe()}");

        for (var i = 0; i < 3; i++)
        {
            await PayLndAsync(node, david, channelId, channelPoint, 10_000, ct);
            await LndPaysUsAsync(node, david, lndChannel.ChanId, channelId, channelPoint, 5_000, ct);
        }

        var now = await node.GetChannelAsync(channelId, ct);
        Assert.True(now.RemoteCommitmentNumber > snapshotState.RemoteCommitmentNumber + 1, now.Describe());
        var walletBefore = WalletBalance(node);
        await node.StopAsync();

        await rollback.RestoreSnapshotAsync(ct);
        var parts = channelPoint.Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        // LND answers GetInfo (synced) before its server is started, and refuses CloseChannel until then
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
        var revoked = await _fixture.Bitcoin.GetRawTransactionAsync(new uint256(pending.Txid.ToByteArray()), true, ct);
        Console.WriteLine($"[o5a] david force-closed with {revoked.GetHash()}");
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [], ct);
        await node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [node], ct);
        return (node, david, channelId, channelPoint, revoked, walletBefore);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name);
        _nodes.Add(node);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>
    /// The victim recorded the funding spend as a revoked commitment, penalized every output of it, the penalties
    /// confirmed, and its wallet gained exactly their outputs: the capacity less the commitment fee and the penalty
    /// fees.
    /// </summary>
    private async Task AssertPenalizedAsync(NLightningTestNode victim, ChannelId channelId, Transaction revoked,
                                            LightningMoney walletBefore, CancellationToken ct)
    {
        TxId revokedTxId = revoked.GetHash().ToBytes();
        var close = await Poll.ForAsync(() => GetCloseAsync(victim, channelId), s_timeout,
                                        "the victim recorded the funding spend", ct);
        Assert.Equal(ChannelCloseKind.RevokedCommitment, close.Kind);
        Assert.Equal(revokedTxId, close.CommitmentTransactionId);

        // Mine one block at a time until every output of the revoked commitment is spent by one of our penalties
        var penalties = new Dictionary<uint256, Transaction>();
        await Poll.UntilAsync(async () =>
        {
            foreach (var row in await GetOutputsAsync(victim, channelId))
            {
                if (row.ResolvingTransactionId is not { } txId)
                    continue;
                var hash = new uint256((byte[])txId);
                if (penalties.ContainsKey(hash))
                    continue;
                try
                {
                    penalties[hash] = await _fixture.Bitcoin.GetRawTransactionAsync(hash, true, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not in the mempool or a block (yet, or replaced)
                }
            }

            var spent = new HashSet<OutPoint>(penalties.Values.SelectMany(p => p.Inputs.Select(i => i.PrevOut)));
            var allSpent = Enumerable.Range(0, revoked.Outputs.Count)
                                     .All(v => spent.Contains(new OutPoint(revoked.GetHash(), v)));
            var confirmed = allSpent && await AllConfirmedAsync(penalties.Keys, revoked, ct);
            if (!confirmed)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [victim], ct);
            return confirmed;
        }, s_timeout, "every output of the revoked commitment penalized and confirmed", ct);

        var used = penalties.Values.Where(p => p.Inputs.Any(i => i.PrevOut.Hash == revoked.GetHash())).ToList();
        foreach (var penalty in used)
            Console.WriteLine($"[o5] penalty {penalty.GetHash()}: {penalty.Inputs.Count} input(s), "
                            + $"{penalty.Outputs.Sum(o => o.Value.Satoshi)} sat out");

        var commitmentFee = (long)s_capacity.Satoshi - revoked.Outputs.Sum(o => o.Value.Satoshi);
        var gained = used.Sum(p => p.Outputs.Sum(o => o.Value.Satoshi));
        var penaltyFees = revoked.Outputs.Sum(o => o.Value.Satoshi) - gained;
        Console.WriteLine($"[o5] commitment fee {commitmentFee} sat, penalty fees {penaltyFees} sat, gained {gained} sat");
        Assert.InRange(penaltyFees, 1, (long)s_capacity.Satoshi / 100);
        Assert.Equal((long)s_capacity.Satoshi - commitmentFee - penaltyFees, gained);

        await Poll.UntilAsync(() => Task.FromResult((WalletBalance(victim) - walletBefore).Satoshi == gained),
                              s_timeout, $"the victim's wallet gained {gained} sat", ct);
    }

    private async Task<bool> AllConfirmedAsync(IEnumerable<uint256> txIds, Transaction revoked, CancellationToken ct)
    {
        foreach (var txId in txIds)
        {
            var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(txId, ct);
            if (info.Confirmations == 0 && info.Transaction.Inputs.Any(i => i.PrevOut.Hash == revoked.GetHash()))
                return false;
        }

        return true;
    }

    private static async Task<ChannelCloseModel?> GetCloseAsync(NLightningTestNode node, ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetCloseAsync(channelId);
    }

    private static async Task<IReadOnlyList<OutputResolutionModel>> GetOutputsAsync(NLightningTestNode node,
                                                                                    ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetOutputsByChannelIdAsync(channelId);
    }

    private static LightningMoney WalletBalance(NLightningTestNode node)
    {
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var height = node.BlockchainMonitor.LastProcessedBlockHeight;
        return utxos.GetConfirmedBalance(height) + utxos.GetUnconfirmedBalance(height);
    }

    /// <summary>Mines one block at a time until the channel is usable at every NLightning end and active in LND.</summary>
    private async Task WaitUsableAsync(IReadOnlyList<LNDNodeConnection> lndNodes,
                                       IReadOnlyList<NLightningTestNode> nodes, ChannelId channelId,
                                       CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var usable = true;
            foreach (var node in nodes)
                usable &= (await node.GetChannelAsync(channelId, ct)).IsUsable();
            foreach (var lnd in lndNodes)
            {
                var channels = await lnd.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                          cancellationToken: ct);
                usable &= channels.Channels.Any(c => c.Active && nodes.Any(n => n.NodeIdHex == c.RemotePubkey));
            }

            if (!usable)
                await ChainSync.MineAndWaitAsync(_fixture, 1, lndNodes, nodes, ct);
            return usable;
        }, s_timeout, $"channel {channelId} usable", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, lndNodes, nodes, ct);
    }

    /// <summary><paramref name="payer"/> pays <paramref name="payee"/>'s invoice over their channel, and both sides
    /// settle.</summary>
    private static async Task PayAsync(NLightningTestNode payer, NLightningTestNode payee, ChannelId channelId,
                                       long amountSat, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), "o5 payment", ct);
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

    /// <summary>We pay david <paramref name="amountSat"/> and wait until both sides settled.</summary>
    private static async Task PayLndAsync(NLightningTestNode node, LNDNodeConnection david, ChannelId channelId,
                                          string channelPoint, long amountSat, CancellationToken ct)
    {
        var invoice = await LndTestHelpers.AddInvoiceAsync(david, amountSat * 1_000, [], ct, "o5 we pay david");
        var payment = await node.PayInvoiceAsync(invoice.PaymentRequest, ct);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await WaitSettledWithLndAsync(node, david, channelId, channelPoint, ct);
    }

    /// <summary>david pays our invoice of <paramref name="amountSat"/> over the channel, and both sides settle.</summary>
    private static async Task LndPaysUsAsync(NLightningTestNode node, LNDNodeConnection david, ulong chanId,
                                             ChannelId channelId, string channelPoint, long amountSat,
                                             CancellationToken ct)
    {
        var invoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), "o5 david pays us", ct);
        Lnrpc.Payment? payment = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            // Right after a restart or a settled payment LND may not route to us yet (no route); try again
            await LndTestHelpers.ResetMissionControlAsync(david, ct);
            payment = await LndTestHelpers.SendPaymentV2Async(
                          david, LndTestHelpers.PinnedPayment(invoice.Bolt11, [chanId]), ct);
            if (payment.Status == Lnrpc.Payment.Types.PaymentStatus.Succeeded)
                break;

            Console.WriteLine($"[o5a] david's payment attempt {attempt} failed: {payment.FailureReason}");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        Assert.Equal(Lnrpc.Payment.Types.PaymentStatus.Succeeded, payment!.Status);
        await WaitSettledWithLndAsync(node, david, channelId, channelPoint, ct);
    }

    private static async Task WaitSettledWithLndAsync(NLightningTestNode node, LNDNodeConnection david,
                                                      ChannelId channelId, string channelPoint, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(david, channelPoint, ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && theirs is not null && theirs.PendingHtlcs.Count == 0
                && theirs.LocalBalance == (long)ours.RemoteBalance.Satoshi;
        }, s_timeout, "the payment settled with david", ct);
    }
}