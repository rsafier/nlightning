using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Application.Channels.Safety.Interfaces;
using Cheater;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// Proof SP2 (d)/(e) of the splicing plan (<c>docs/agents/SPLICING_PLAN.md</c> §3.6, wave sp2 lane SP2-C, NL-021,
/// NL-479): BOLT 5 across fundings between two NLightning nodes on the shared regtest bitcoind. Alice funds the channel
/// and splices 100,000 sat in; Bob accepts with no contribution. Both run the experimental <c>option_quiesce</c> and
/// <c>option_splice</c> (Optional) with <c>option_static_remotekey</c> channels (<see cref="LegacyChannelOptions"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>(a) force close on the old funding while the splice is pending: the splice is evicted from bitcoind's mempool
/// (<c>setmocktime</c> expiry, as <c>OnchainO6Tests</c>), Alice fails the channel and her commitment on the old funding
/// confirms; both ends record the close on the old funding and discard the splice (funding row Discarded, its
/// broadcast abandoned).</item>
/// <item>(b) force close when the splice confirmed first: the splice is mined (depth 1, not locked), Alice fails the
/// channel (her commitment on the old funding can no longer confirm); her failure service broadcasts her commitment on
/// the splice funding at the next block (SP-I4); both ends classify it against the splice funding (Alice: our local
/// commitment, Bob: the peer's commitment) and Bob's <c>to_remote</c> is swept.</item>
/// <item>(c) a revoked commitment of the old funding after the lock: Alice's commitment k on the old funding is captured
/// (<see cref="StaleCommitmentCapture"/>), the splice locks, three payments revoke k, Alice stops; the splice's block is
/// invalidated and a block holding k is mined instead: Bob classifies k against the old funding (a retired funding,
/// SP-I5) and penalizes every output.</item>
/// <item>(d) a pending splice reorged out and mined again: never a close, and it locks.</item>
/// <item>(e) a locked splice reorged out: the channel keeps operating (a payment each way), no close, and the splice
/// confirms again.</item>
/// </list>
/// Run with <c>scripts/run-onchain.sh 1 Release -class NLightning.Integration.Tests.Docker.Onchain.OnchainSpliceTests</c>
/// (own process, the in-container runner with <c>--network host</c>).
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
public sealed class OnchainSpliceTests : IAsyncLifetime
{
    private const ulong CapacitySat = 1_000_000;
    private const ulong PushSat = 100_000;
    private const ulong SpliceInSat = 100_000;
    private const uint FeeRatePerKw = 2_500;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public OnchainSpliceTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Proof SP2 (d) part 1 and the lane proof "force close on the old funding while a splice is pending".</summary>
    [Fact]
    public async Task Given_ASplicePending_When_OurCommitmentOnTheOldFundingConfirms_Then_BothEndsDiscardTheSplice()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (alice, bob, channelId, oldFunding) = await OpenAsync("sp2a", ct);
        var splice = await SpliceInAsync(alice, channelId, ct);
        await WaitInMempoolAsync(splice, ct);

        // Act: the splice leaves the mempool, Alice force-closes, her commitment on the old funding confirms
        await EvictMempoolAsync(ct);
        var outcome = await FailAsync(alice, channelId, ct);
        await WaitInMempoolAsync(outcome, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);

        // Assert: both ends closed on the old funding, the splice discarded and its broadcast abandoned
        var aliceClose = await WaitCloseAsync(alice, channelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, aliceClose.Kind);
        Assert.Equal(outcome, aliceClose.CommitmentTransactionId);
        var bobClose = await WaitCloseAsync(bob, channelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, bobClose.Kind);
        foreach (var node in new[] { alice, bob })
        {
            await Poll.UntilAsync(async () => (await GetFundingsAsync(node, channelId))
                                             .Single(f => f.FundingTxId == splice).Status
                                         == ChannelFundingStatus.Discarded,
                                  s_timeout, $"{node.Name}: the splice discarded", ct);
            var spliceRow = (await GetBroadcastsAsync(node, channelId)).Single(b => b.TransactionId == splice);
            Assert.Equal(BroadcastState.Abandoned, spliceRow.State);
            Assert.Equal(ChannelState.OnchainResolving, (await node.GetChannelAsync(channelId, ct)).State);
        }

        Assert.Equal(oldFunding, SpentOutpointOf(await GetRawAsync(outcome, ct)).Hash);
    }

    /// <summary>
    /// Proof SP2 (d) part 2 and the lane proof "force close on the pending splice funding" (ours for Alice, theirs for
    /// Bob).
    /// </summary>
    [Fact]
    public async Task Given_TheSpliceConfirmedFirst_When_AliceForceCloses_Then_HerCommitmentOnTheSpliceIsResolved()
    {
        // Arrange: the splice is mined (depth 1, not locked)
        var ct = TestContext.Current.CancellationToken;
        var (alice, bob, channelId, _) = await OpenAsync("sp2b", ct);
        var splice = await SpliceInAsync(alice, channelId, ct);
        await WaitInMempoolAsync(splice, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
        var bobWalletBefore = WalletBalance(bob);

        // Act: Alice fails the channel; her commitment on the old funding is refused (the splice spent its input),
        // and the next block has her failure service broadcast her commitment on the splice funding (SP-I4)
        var onOld = await FailAsync(alice, channelId, ct);
        var onSplice = await Poll.ForAsync(async () =>
        {
            var row = (await GetBroadcastsAsync(alice, channelId))
                     .FirstOrDefault(b => b.Purpose == BroadcastPurpose.LocalCommitment
                                       && SpentOutpointOf(Transaction.Load(b.RawTransaction, Network.RegTest)).Hash
                                       == new uint256((byte[])splice));
            if (row is null)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
            return row;
        }, s_timeout, "Alice's commitment on the splice funding", ct);
        Assert.Equal(onSplice.CommitmentNumber,
                     (await GetBroadcastsAsync(alice, channelId)).Single(b => b.TransactionId == onOld)
                                                                 .CommitmentNumber);
        await WaitInMempoolAsync(onSplice.TransactionId, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);

        // Assert: Alice's local close and Bob's remote close, both on the splice funding; Bob's to_remote swept
        var aliceClose = await WaitCloseAsync(alice, channelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, aliceClose.Kind);
        Assert.Equal(onSplice.TransactionId, aliceClose.CommitmentTransactionId);
        Assert.Contains(await GetOutputsAsync(alice, channelId),
                        o => o.TransactionId == onSplice.TransactionId
                          && o.Descriptor == OutputDescriptorKind.DelayedToLocal);
        Assert.Equal(BroadcastState.Abandoned,
                     (await GetBroadcastsAsync(alice, channelId)).Single(b => b.TransactionId == onOld).State);
        var bobClose = await WaitCloseAsync(bob, channelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, bobClose.Kind);
        Assert.Equal(onSplice.TransactionId, bobClose.CommitmentTransactionId);
        var toRemote = await Poll.ForAsync(async () => (await GetOutputsAsync(bob, channelId))
                                                      .FirstOrDefault(o => o.Descriptor
                                                                        == OutputDescriptorKind.PaymentToRemote),
                                           s_timeout, "Bob's to_remote row", ct);
        var commitment = await GetRawAsync(onSplice.TransactionId, ct);
        var toRemoteSat = commitment.Outputs[(int)toRemote.OutputIndex].Value.Satoshi;
        Assert.True(toRemoteSat > (long)PushSat / 2, $"Bob's to_remote is {toRemoteSat} sat");
        await Poll.UntilAsync(async () =>
        {
            var gained = (WalletBalance(bob) - bobWalletBefore).Satoshi;
            if (gained <= 0)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
            return gained > 0 && gained <= toRemoteSat;
        }, s_timeout, "Bob's to_remote swept to his wallet", ct);
    }

    /// <summary>
    /// Proof SP2 (d) part 3 and the lane proof "a revoked commitment of the old funding after a splice gets a penalty"
    /// (NL-479, SP-I5).
    /// </summary>
    [Fact]
    public async Task Given_ALockedSpliceReorgedOut_When_ARevokedCommitmentOfTheOldFundingConfirms_Then_Penalized()
    {
        // Arrange: Alice's commitment k on the old funding captured; the splice locks; three payments revoke k
        var ct = TestContext.Current.CancellationToken;
        var (alice, bob, channelId, oldFunding) = await OpenAsync("sp2c", ct);
        await PayAsync(alice, bob, channelId, 20_000, ct);
        var captured = await StaleCommitmentCapture.CaptureAsync(alice, channelId);
        Assert.Equal(oldFunding, SpentOutpointOf(captured.Transaction).Hash);
        var splice = await SpliceInAsync(alice, channelId, ct);
        await WaitInMempoolAsync(splice, ct);
        var spliceHeight = await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
        var spliceBlock = await _fixture.Bitcoin.GetBlockHashAsync((int)spliceHeight, ct);
        await MineUntilLockedAsync(alice, bob, channelId, splice, ct);
        Assert.Null(await GetCloseAsync(bob, channelId));
        for (var i = 0; i < 3; i++)
            await PayAsync(alice, bob, channelId, 30_000, ct);
        var now = await bob.GetChannelAsync(channelId, ct);
        Assert.True(now.RemoteCommitmentNumber > captured.Number + 1, $"k = {captured.Number} is not revoked yet");
        await alice.StopAsync();
        var walletBefore = WalletBalance(bob);

        // Act: the splice's block leaves the chain and a block with k takes its place
        await _fixture.Bitcoin.InvalidateBlockAsync(spliceBlock, ct);
        var address = await _fixture.Bitcoin.GetNewAddressAsync(ct);
        await _fixture.Bitcoin.SendCommandAsync("generateblock", ct, address.ToString(),
                                                new[] { captured.Transaction.ToHex() });
        Console.WriteLine($"[sp2c] k = {captured.Transaction.GetHash()} mined instead of splice {Display(splice)}");
        await ChainSync.MineAndWaitAsync(_fixture, 2, [], [bob], ct);

        // Assert: Bob recorded the breach of the old funding and penalized every output of k
        var close = await WaitCloseAsync(bob, channelId, ct);
        Assert.Equal(ChannelCloseKind.RevokedCommitment, close.Kind);
        Assert.Equal((TxId)captured.Transaction.GetHash().ToBytes(), close.CommitmentTransactionId);
        var revoked = captured.Transaction;
        var penalties = new Dictionary<uint256, Transaction>();
        await Poll.UntilAsync(async () =>
        {
            foreach (var row in await GetOutputsAsync(bob, channelId))
            {
                if (row.ResolvingTransactionId is not { } txId || penalties.ContainsKey(new uint256((byte[])txId)))
                    continue;
                try
                {
                    penalties[new uint256((byte[])txId)] = await GetRawAsync(txId, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not broadcast yet
                }
            }

            var spent = penalties.Values.SelectMany(p => p.Inputs.Select(i => i.PrevOut)).ToHashSet();
            var all = Enumerable.Range(0, revoked.Outputs.Count)
                                .All(v => spent.Contains(new OutPoint(revoked.GetHash(), v)));
            var confirmed = all && await AllConfirmedAsync(penalties.Keys, ct);
            if (!confirmed)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [bob], ct);
            return confirmed;
        }, s_timeout, "every output of k penalized and confirmed", ct);
        var gained = penalties.Values.Where(p => p.Inputs.Any(i => i.PrevOut.Hash == revoked.GetHash()))
                              .Sum(p => p.Outputs.Sum(o => o.Value.Satoshi));
        Assert.InRange(revoked.Outputs.Sum(o => o.Value.Satoshi) - gained, 1, (long)CapacitySat / 100);
        await Poll.UntilAsync(() => Task.FromResult((WalletBalance(bob) - walletBefore).Satoshi >= gained),
                              s_timeout, $"Bob's wallet gained {gained} sat", ct);
    }

    /// <summary>Proof SP2 (e), before the lock: the pending splice is reorged out and mined again.</summary>
    [Fact]
    public async Task Given_APendingSplice_When_ItsBlockIsReorgedOut_Then_NoCloseAndItLocksOnceMinedAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (alice, bob, channelId, _) = await OpenAsync("sp2d", ct);
        var splice = await SpliceInAsync(alice, channelId, ct);
        await WaitInMempoolAsync(splice, ct);
        var height = await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);

        // Act: the splice's block is invalidated (the splice goes back to the mempool) and the chain goes on
        await _fixture.Bitcoin.InvalidateBlockAsync(await _fixture.Bitcoin.GetBlockHashAsync((int)height, ct), ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [], [alice, bob], ct);
        await MineUntilLockedAsync(alice, bob, channelId, splice, ct);

        // Assert
        foreach (var node in new[] { alice, bob })
        {
            Assert.Null(await GetCloseAsync(node, channelId));
            Assert.Equal(ChannelState.Open, (await node.GetChannelAsync(channelId, ct)).State);
        }

        await PayAsync(alice, bob, channelId, 10_000, ct);
    }

    /// <summary>
    /// Proof SP2 (e), after the lock: the locked splice is reorged out and kept out of one block; the channel keeps
    /// operating on the splice funding (payments both ways) and never closes; the splice confirms again.
    /// </summary>
    [Fact]
    public async Task Given_ALockedSplice_When_ItsBlockIsReorgedOut_Then_TheChannelKeepsOperating()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (alice, bob, channelId, oldFunding) = await OpenAsync("sp2e", ct);
        var splice = await SpliceInAsync(alice, channelId, ct);
        await WaitInMempoolAsync(splice, ct);
        var height = await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
        var block = await _fixture.Bitcoin.GetBlockHashAsync((int)height, ct);
        await MineUntilLockedAsync(alice, bob, channelId, splice, ct);

        // Act: the splice leaves the chain and the mempool; an empty block takes its place
        await _fixture.Bitcoin.InvalidateBlockAsync(block, ct);
        await EvictMempoolAsync(ct);
        var address = await _fixture.Bitcoin.GetNewAddressAsync(ct);
        await _fixture.Bitcoin.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        await ChainSync.WaitAllAtTipAsync(_fixture, [], [alice, bob], ct);

        // Assert: payments go on, no close; the old funding is unspent until the splice (rebroadcast) confirms again
        await PayAsync(alice, bob, channelId, 10_000, ct);
        await PayAsync(bob, alice, channelId, 5_000, ct);
        foreach (var node in new[] { alice, bob })
            Assert.Null(await GetCloseAsync(node, channelId));
        await Poll.UntilAsync(async () =>
        {
            var spent = await _fixture.Bitcoin.GetTxOutAsync(oldFunding, 0, false) is null
                     && (await _fixture.Bitcoin.GetRawTransactionInfoAsync(new uint256((byte[])splice), ct))
                        .Confirmations > 0;
            if (!spent)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
            return spent;
        }, s_timeout, "the splice confirmed again", ct);
        foreach (var node in new[] { alice, bob })
        {
            Assert.Null(await GetCloseAsync(node, channelId));
            Assert.Equal(ChannelState.Open, (await node.GetChannelAsync(channelId, ct)).State);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A failed test must not leave the mock time set
        await _fixture.Bitcoin.SendCommandAsync("setmocktime", CancellationToken.None, 0);
        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    #region Setup

    /// <summary>Alice and Bob with splicing, Alice funds 1,000,000 sat with 100,000 pushed; usable at both ends.</summary>
    private async Task<(NLightningTestNode Alice, NLightningTestNode Bob, ChannelId ChannelId, uint256 Funding)>
        OpenAsync(string prefix, CancellationToken ct)
    {
        var alice = await CreateNodeAsync($"{prefix}-alice", ct);
        var bob = await CreateNodeAsync($"{prefix}-bob", ct);
        await alice.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [], [alice, bob], ct);
        await alice.ConnectToAsync(bob, ct);
        var opened = await alice.OpenChannelAsync(new OpenChannelClientRequest(bob.Address,
                                                                               LightningMoney.Satoshis(CapacitySat))
        {
            PushAmount = LightningMoney.Satoshis(PushSat),
            FeeRatePerKw = LightningMoney.Satoshis(FeeRatePerKw)
        }, ct);
        var channelId = opened.ChannelId;
        await Poll.UntilAsync(async () =>
        {
            var usable = (await alice.GetChannelAsync(channelId, ct)).IsUsable()
                      && (await bob.GetChannelAsync(channelId, ct)).IsUsable();
            if (!usable)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
            return usable;
        }, s_timeout, $"channel {channelId} usable", ct);
        Assert.True(alice.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        return (alice, bob, channelId, new uint256((byte[])channel!.FundingOutput!.TransactionId!.Value));
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name,
            configureNodeOptions: LegacyChannelOptions.PinStaticRemoteKey(o =>
            {
                o.Features.AllowExperimentalFeatures = true;
                o.Features.OptionQuiesce = FeatureSupport.Optional;
                o.Features.OptionSplice = FeatureSupport.Optional;
            }));
        node.ConfigureServices = services => services.AddSpliceIpcServices();
        _nodes.Add(node);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>Alice splices <see cref="SpliceInSat"/> in through the daemon's <c>splicein</c>; returns the splice.</summary>
    private static async Task<TxId> SpliceInAsync(NLightningTestNode node, ChannelId channelId, CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<SpliceInClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new SpliceInClientRequest(channelId, SpliceInSat)
        {
            FeeRatePerKw = FeeRatePerKw
        }, ct);
        Console.WriteLine($"[{node.Name}] splicein: {response.State}, txid {response.SpliceTxId}, capacity "
                        + $"{response.NewCapacitySat}, reason {response.FailureReason}");
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        return response.SpliceTxId!.Value;
    }

    /// <summary>Mines one block at a time until both ends run on the splice funding (locked both ways).</summary>
    private async Task MineUntilLockedAsync(NLightningTestNode alice, NLightningTestNode bob, ChannelId channelId,
                                            TxId splice, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var locked = new[] { alice, bob }.All(n => n.ChannelMemoryRepository.TryGetChannel(channelId, out var c)
                                                     && c!.FundingOutput?.TransactionId == splice);
            if (!locked)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [], [alice, bob], ct);
            return locked;
        }, s_timeout, $"splice {Display(splice)} locked on both ends", ct);
        await Poll.UntilAsync(async () => (await alice.GetChannelAsync(channelId, ct)).IsUsable()
                                       && (await bob.GetChannelAsync(channelId, ct)).IsUsable(),
                              s_timeout, "the channel usable after the lock", ct);
    }

    #endregion

    #region Actions and reads

    private static async Task<TxId> FailAsync(NLightningTestNode node, ChannelId channelId, CancellationToken ct)
    {
        var outcome = await node.Services.GetRequiredService<IChannelFailureService>()
                                .FailChannelAsync(channelId,
                                                  new ChannelFailureRequest("SP2 proof force close",
                                                                            "force closing the channel"), ct);
        Console.WriteLine($"[{node.Name}] force close: {outcome.Status} {outcome.CommitmentTxId}");
        Assert.NotNull(outcome.CommitmentTxId);
        return outcome.CommitmentTxId.Value;
    }

    private static async Task PayAsync(NLightningTestNode payer, NLightningTestNode payee, ChannelId channelId,
                                       long amountSat, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), "sp2 payment", ct);
        var payment = await payer.PayInvoiceAsync(invoice.Bolt11!, ct);
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

    /// <summary>Evicts every transaction from bitcoind's mempool by expiry (<c>OnchainO6Tests</c>).</summary>
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

    private async Task WaitInMempoolAsync(TxId txId, CancellationToken ct)
    {
        var display = new uint256((byte[])txId);
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(display),
                              s_timeout, $"{display} in the mempool", ct);
    }

    private async Task<Transaction> GetRawAsync(TxId txId, CancellationToken ct) =>
        await _fixture.Bitcoin.GetRawTransactionAsync(new uint256((byte[])txId), true, ct);

    private async Task<bool> AllConfirmedAsync(IEnumerable<uint256> txIds, CancellationToken ct)
    {
        foreach (var txId in txIds)
        {
            if ((await _fixture.Bitcoin.GetRawTransactionInfoAsync(txId, ct)).Confirmations == 0)
                return false;
        }

        return true;
    }

    private static OutPoint SpentOutpointOf(Transaction transaction) => transaction.Inputs[0].PrevOut;

    private static async Task<ChannelCloseModel> WaitCloseAsync(NLightningTestNode node, ChannelId channelId,
                                                                CancellationToken ct) =>
        await Poll.ForAsync(() => GetCloseAsync(node, channelId), s_timeout, $"{node.Name}: the close recorded", ct);

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

    private static async Task<IReadOnlyList<BroadcastTransactionModel>> GetBroadcastsAsync(NLightningTestNode node,
                                                                                           ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                          .GetByChannelIdAsync(channelId);
    }

    private static async Task<IReadOnlyList<ChannelFunding>> GetFundingsAsync(NLightningTestNode node,
                                                                              ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelFundingDbRepository
                          .GetByChannelIdAsync(channelId);
    }

    private static LightningMoney WalletBalance(NLightningTestNode node)
    {
        var utxos = node.Services.GetRequiredService<Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>();
        var height = node.BlockchainMonitor.LastProcessedBlockHeight;
        return utxos.GetConfirmedBalance(height) + utxos.GetUnconfirmedBalance(height);
    }

    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();

    #endregion
}