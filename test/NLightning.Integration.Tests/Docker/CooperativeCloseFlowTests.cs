using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Application.Channels.Close;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// Proof N10 (BOLT2 plan): cooperative close against LND. A channel we funded (with a push to alice) carries a
/// payment each way, then is closed by us and by alice, with and without <c>fee_range</c>; the agreed closing
/// transaction confirms, LND lists a <c>COOPERATIVE_CLOSE</c> with the same txid and alice's settled balance, our
/// side becomes Closed after 6 blocks and our wallet receives our output.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class CooperativeCloseFlowTests : IAsyncLifetime
{
    private const long FundingSat = 1_000_000;
    private const long PushSat = 200_000;
    private const long WePaySat = 30_000;
    private const long AlicePaysSat = 10_000;

    /// <summary>
    /// Alice's side of the channel after the push and the two payments (the funder pays the fees).
    /// </summary>
    private const long AliceShareSat = PushSat + WePaySat - AlicePaysSat;

    private static readonly TimeSpan s_activeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(60);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public CooperativeCloseFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "closer");
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_OpenChannel_When_WeCloseCooperatively_Then_ClosedOnBothSidesAndFundsBack(
        bool withFeeRange)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        var (channelId, channelPoint) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        await MakePaymentsAsync(node, alice, channelId, channelPoint, ct);
        var walletBefore = WalletBalance(node);

        // Act
        CloseChannelClientResponse closed;
        using (var scope = node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                    CloseChannelClientResponse>>();
            closed = await handler.HandleAsync(new CloseChannelClientRequest(channelId)
            {
                NoFeeRange = !withFeeRange,
                WaitSeconds = (uint)s_closeTimeout.TotalSeconds
            }, ct);
        }

        // Assert
        Assert.Equal(ChannelState.Closing, closed.State);
        Assert.NotNull(closed.ClosingTxId);
        var closingTx = GetClosingTransaction(node, channelId);
        Assert.NotNull(closingTx);
        Assert.Equal(closed.ClosingTxId.Value, closingTx.TxId);
        await AssertClosedAsync(node, alice, channelId, channelPoint, closingTx, walletBefore, Initiator.Remote, ct);
        Assert.True(node.CountLogLines("closing_signed for channel") > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_OpenChannel_When_LndClosesCooperatively_Then_WeReplyNegotiateAndClose(bool withFeeRange)
    {
        // Arrange: we funded the channel, so we propose the fee after alice's shutdown (B2-CLS-01)
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        node.Services.GetRequiredService<IOptions<ChannelCloseOptions>>().Value.SendFeeRange = withFeeRange;
        var (channelId, channelPoint) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        await MakePaymentsAsync(node, alice, channelId, channelPoint, ct);
        var walletBefore = WalletBalance(node);

        // Act: alice closes (the stream reports the pending close; the close goes on without it)
        var parts = channelPoint.Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_closeTimeout);
        using var closeCall = alice.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint
            {
                FundingTxidStr = parts[0],
                OutputIndex = uint.Parse(parts[1])
            }
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);

        // Assert
        var closingTx = await Poll.ForAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.Single(c => c.ChannelId == channelId);
            return ours.State == ChannelState.Closing ? GetClosingTransaction(node, channelId) : null;
        }, s_closeTimeout, "our channel is Closing", ct);
        // LND's ClosePending txid is in internal byte order, like ours
        Assert.Equal(Convert.ToHexString(pending.Txid.ToByteArray()).ToLowerInvariant(),
                     Convert.ToHexString((byte[])closingTx.TxId).ToLowerInvariant());
        await AssertClosedAsync(node, alice, channelId, channelPoint, closingTx, walletBefore, Initiator.Local, ct);
    }

    #region option_simple_close (BOLT2 plan N11)

    [Fact]
    public async Task Given_SimpleCloseNegotiated_When_WeClose_Then_EachSideSignsTheOthersTransactionAndOneConfirms()
    {
        // Arrange: alice runs --protocol.rbf-coop-close (LND's option_simple_close, bit 61) and so do we
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        await using var node = await CreateSimpleCloseNodeAsync(ct);
        var (channelId, channelPoint) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        await MakePaymentsAsync(node, alice, channelId, channelPoint, ct);
        var walletBefore = WalletBalance(node);

        // Act
        CloseChannelClientResponse closed;
        using (var scope = node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                    CloseChannelClientResponse>>();
            closed = await handler.HandleAsync(new CloseChannelClientRequest(channelId)
            {
                WaitSeconds = (uint)s_closeTimeout.TotalSeconds
            }, ct);
        }

        // Assert: closing_complete both ways, each signed by the other side; no closing_signed
        Assert.Equal(ChannelState.Closing, closed.State);
        Assert.NotNull(closed.ClosingTxId);
        var ourScript = await WaitForSimpleCloseExchangeAsync(node, channelId, 1, 1, ct);
        Assert.Equal(0, node.CountLogLines("closing_signed for channel"));
        await AssertSimpleClosedAsync(node, alice, channelId, channelPoint, ourScript, walletBefore, ct);
    }

    [Fact]
    public async Task Given_SimpleCloseNegotiated_When_LndCloses_Then_WeReplySignAndClose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        await using var node = await CreateSimpleCloseNodeAsync(ct);
        var (channelId, channelPoint) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        await MakePaymentsAsync(node, alice, channelId, channelPoint, ct);
        var walletBefore = WalletBalance(node);

        // Act: alice closes at 5 sat/vB (her closing_complete pays it)
        await LndCloseAsync(alice, channelPoint, 5, ct);

        // Assert
        var ourScript = await WaitForSimpleCloseExchangeAsync(node, channelId, 1, 1, ct);
        Assert.Equal(0, node.CountLogLines("closing_signed for channel"));
        await AssertSimpleClosedAsync(node, alice, channelId, channelPoint, ourScript, walletBefore, ct);
    }

    [Fact]
    public async Task Given_SimpleClose_When_BothSidesBumpTheFee_Then_NewTransactionsSignedAndOneConfirms()
    {
        // Arrange: a simple close we started
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        await using var node = await CreateSimpleCloseNodeAsync(ct);
        var (channelId, channelPoint) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        await MakePaymentsAsync(node, alice, channelId, channelPoint, ct);
        var walletBefore = WalletBalance(node);
        var closeService = node.Services.GetRequiredService<IChannelCloseService>();
        await closeService.CloseChannelAsync(channelId, new ChannelCloseRequest(WaitFor: s_closeTimeout), ct);
        await WaitForSimpleCloseExchangeAsync(node, channelId, 1, 1, ct);

        // Act 1: we RBF ours at 20,000 sat/kw (a new closing_complete; alice signs it)
        var bumped = await closeService.CloseChannelAsync(channelId, new ChannelCloseRequest(FeeRatePerKw: 20_000),
                                                          ct);
        Assert.Equal(ChannelState.Closing, bumped.State);
        await WaitForSimpleCloseExchangeAsync(node, channelId, 2, 1, ct);
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        var ourTx = NBitcoin.Transaction.Load(channel.ClosingTransaction!.RawTxBytes, Network.RegTest);
        var ourFee = FundingSat - ourTx.Outputs.Sum(o => o.Value.Satoshi);
        // Our fee at 20,000 sat/kw for our P2WPKH output and alice's script (LND pays to P2TR by default)
        var weight = ClosingFeeCalculator.EstimateWeight(channel.LocalShutdownScript!.Value.Length,
                                                         channel.RemoteShutdownScript!.Value.Length);
        Assert.Equal((long)ClosingFeeCalculator.FeeSat(20_000, weight), ourFee);
        Assert.Equal(FundingSat - AliceShareSat - ourFee,
                     ourTx.Outputs.Where(o => o.ScriptPubKey.ToBytes()
                                               .SequenceEqual((byte[])channel.LocalShutdownScript!.Value))
                          .Sum(o => o.Value.Satoshi));

        // Act 2: alice RBFs hers at 60 sat/vB (15,000 sat/kw; we sign it)
        await LndCloseAsync(alice, channelPoint, 60, ct);

        // Assert
        var ourScript = await WaitForSimpleCloseExchangeAsync(node, channelId, 2, 2, ct);
        await AssertSimpleClosedAsync(node, alice, channelId, channelPoint, ourScript, walletBefore, ct);
    }

    /// <summary>
    /// A node that negotiates <c>option_simple_close</c> (and its BOLT 9 dependency <c>option_shutdown_anysegwit</c>).
    /// </summary>
    private async Task<NLightningTestNode> CreateSimpleCloseNodeAsync(CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, "simple", configureNodeOptions: options =>
        {
            options.Features.OptionSimpleClose = FeatureSupport.Optional;
            options.Features.BeyondSegwitShutdown = FeatureSupport.Optional;
        });
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>LND's <c>CloseChannel</c> at <paramref name="satPerVbyte"/>, read until it reports the pending close.
    /// </summary>
    private static async Task LndCloseAsync(LNDNodeConnection alice, string channelPoint, ulong satPerVbyte,
                                            CancellationToken ct)
    {
        var parts = channelPoint.Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_closeTimeout);
        using var closeCall = alice.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint
            {
                FundingTxidStr = parts[0],
                OutputIndex = uint.Parse(parts[1])
            },
            SatPerVbyte = satPerVbyte
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);
        Console.WriteLine(
            $"LND close pending: {Convert.ToHexString(pending.Txid.ToByteArray())}, local {pending.LocalCloseTx}");
    }

    /// <summary>
    /// Waits until the channel is Closing, alice signed at least <paramref name="ours"/> closing_complete of ours and we
    /// signed at least <paramref name="theirs"/> of hers; returns our output script.
    /// </summary>
    private static async Task<byte[]> WaitForSimpleCloseExchangeAsync(NLightningTestNode node, ChannelId channelId,
                                                                      int ours, int theirs, CancellationToken ct)
    {
        await Poll.UntilAsync(() => Task.FromResult(
                                  node.CountLogLines("The peer signed our closing transaction") >= ours
                               && node.CountLogLines("Signed the peer's closing transaction") >= theirs),
                              s_closeTimeout, $"{ours} of our closing_complete and {theirs} of alice's signed", ct);
        Assert.True(node.CountLogLines("Sending closing_complete") >= ours);
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        Assert.Equal(ChannelState.Closing, channel.State);
        return channel.LocalShutdownScript!.Value;
    }

    /// <summary>
    /// The closing transaction in bitcoind's mempool is a BOLT 3 simple close (version 2, sequence 0xFFFFFFFD); after 6
    /// blocks LND lists a cooperative close with its txid and alice's output as her settled balance, our channel is
    /// Closed and our wallet got our output of it. (LND's close initiator is not asserted: with rbf-coop-close it
    /// reports the side of its own last close action, not the closer of the confirmed transaction.)
    /// </summary>
    private async Task AssertSimpleClosedAsync(NLightningTestNode node, LNDNodeConnection alice, ChannelId channelId,
                                               string channelPoint, byte[] ourScript, LightningMoney walletBefore,
                                               CancellationToken ct)
    {
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        var funding = channel.FundingOutput!;
        var fundingOutPoint = new NBitcoin.OutPoint(new uint256((byte[])funding.TransactionId!.Value), funding.Index!.Value);

        // Conflicting transactions: bitcoind keeps one, the one that confirms
        var closingTx = await Poll.ForAsync(async () =>
        {
            foreach (var txid in await _fixture.Bitcoin.GetRawMempoolAsync(ct))
            {
                var mempoolTx = await _fixture.Bitcoin.GetRawTransactionAsync(txid, true, ct);
                if (mempoolTx.Inputs.Any(i => i.PrevOut == fundingOutPoint))
                    return mempoolTx;
            }

            return null;
        }, s_closeTimeout, "a closing transaction in the mempool", ct);
        Assert.Equal(2U, closingTx.Version);
        Assert.Equal(0xFFFFFFFDU, (uint)Assert.Single(closingTx.Inputs).Sequence);
        var ourOutput = closingTx.Outputs.Where(o => o.ScriptPubKey.ToBytes().SequenceEqual(ourScript))
                                 .Sum(o => o.Value.Satoshi);
        var aliceOutput = closingTx.Outputs.Where(o => !o.ScriptPubKey.ToBytes().SequenceEqual(ourScript))
                                   .Sum(o => o.Value.Satoshi);
        var fee = FundingSat - ourOutput - aliceOutput;
        Console.WriteLine(
            $"Closing transaction {closingTx.GetHash()}: fee {fee} sat, ours {ourOutput}, alice's {aliceOutput}, lock time {(uint)closingTx.LockTime}");
        // BOLT 3: the closer pays the whole fee, the closee gets its whole balance
        var aliceClosed = aliceOutput < AliceShareSat;
        Assert.Equal(aliceClosed ? FundingSat - AliceShareSat : FundingSat - AliceShareSat - fee, ourOutput);
        Assert.Equal(aliceClosed ? AliceShareSat - fee : AliceShareSat, aliceOutput);

        await Poll.UntilAsync(async () =>
        {
            var pendingChannels = await alice.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                                   cancellationToken: ct);
            return pendingChannels.WaitingCloseChannels.Any(c => c.Channel.ChannelPoint == channelPoint);
        }, s_closeTimeout, "alice waits for the closing transaction", ct);
        await node.MineBlocksAsync(6, ct);

        var summary = await Poll.ForAsync(async () =>
        {
            var closedChannels = await alice.LightningClient.ClosedChannelsAsync(
                                     new ClosedChannelsRequest { Cooperative = true }, cancellationToken: ct);
            return closedChannels.Channels.FirstOrDefault(c => c.ChannelPoint == channelPoint);
        }, s_closeTimeout, "alice lists the cooperative close", ct);
        Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, summary.CloseType);
        Assert.Equal(closingTx.GetHash().ToString(), summary.ClosingTxHash);
        Console.WriteLine($"LND close summary: initiator {summary.CloseInitiator}, alice closed: {aliceClosed}");
        Assert.Equal(aliceOutput, summary.SettledBalance);

        await Poll.UntilAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.Single(c => c.ChannelId == channelId);
            return ours.State == ChannelState.Closed;
        }, s_closeTimeout, "our channel is Closed", ct);

        await Poll.UntilAsync(() => Task.FromResult(WalletBalance(node) - walletBefore
                                                 == LightningMoney.Satoshis(ourOutput)),
                              s_closeTimeout, "our wallet received our closing output", ct);
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["alice"]);

        if (_node is not null)
            await _node.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Mines 6 blocks, then: LND lists a cooperative close with our closing txid and the right initiator (from
    /// alice's view: Local when alice closed), alice got her share back, our channel is Closed, and our wallet
    /// received our output (funding - alice's share - fee).
    /// </summary>
    private static async Task AssertClosedAsync(NLightningTestNode node, LNDNodeConnection alice, ChannelId channelId,
                                                string channelPoint, SignedTransaction closingTx,
                                                LightningMoney walletBefore, Initiator initiator,
                                                CancellationToken ct)
    {
        var closingTxHex = Convert.ToHexString(((byte[])closingTx.TxId).Reverse().ToArray()).ToLowerInvariant();
        var tx = NBitcoin.Transaction.Load(closingTx.RawTxBytes, Network.RegTest);
        var fee = FundingSat - tx.Outputs.Sum(o => o.Value.Satoshi);
        var ourOutput = FundingSat - AliceShareSat - fee;
        Assert.InRange(fee, 1, 20_000);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Contains(tx.Outputs, o => o.Value.Satoshi == AliceShareSat);
        Assert.Contains(tx.Outputs, o => o.Value.Satoshi == ourOutput);
        Console.WriteLine($"Closing transaction {closingTxHex}: fee {fee} sat, ours {ourOutput} sat");

        // Both sides broadcast it; alice waits for it to confirm
        await Poll.UntilAsync(async () =>
        {
            var pendingChannels = await alice.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                                   cancellationToken: ct);
            return pendingChannels.WaitingCloseChannels.Any(c => c.Channel.ChannelPoint == channelPoint);
        }, s_closeTimeout, "alice waits for the closing transaction", ct);
        await node.MineBlocksAsync(6, ct);

        var summary = await Poll.ForAsync(async () =>
        {
            var closedChannels = await alice.LightningClient.ClosedChannelsAsync(
                                     new ClosedChannelsRequest { Cooperative = true }, cancellationToken: ct);
            return closedChannels.Channels.FirstOrDefault(c => c.ChannelPoint == channelPoint);
        }, s_closeTimeout, "alice lists the cooperative close", ct);
        Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, summary.CloseType);
        Assert.Equal(closingTxHex, summary.ClosingTxHash);
        Assert.Equal(initiator, summary.CloseInitiator);
        Assert.Equal(AliceShareSat, summary.SettledBalance);

        await Poll.UntilAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.Single(c => c.ChannelId == channelId);
            return ours.State == ChannelState.Closed;
        }, s_closeTimeout, "our channel is Closed", ct);

        await Poll.UntilAsync(() => Task.FromResult(WalletBalance(node) - walletBefore
                                                 == LightningMoney.Satoshis(ourOutput)),
                              s_closeTimeout, "our wallet received our closing output", ct);
    }

    /// <summary>
    /// We pay alice <see cref="WePaySat"/> and alice pays us <see cref="AlicePaysSat"/>, then waits until no HTLC is
    /// left and both sides agree on alice's share, so the close starts from a settled channel.
    /// </summary>
    private static async Task MakePaymentsAsync(NLightningTestNode node, LNDNodeConnection alice, ChannelId channelId,
                                                string channelPoint, CancellationToken ct)
    {
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
        Assert.NotNull(lndChannel);

        var aliceInvoice = await LndTestHelpers.AddInvoiceAsync(alice, WePaySat * 1_000, [], ct, "n10 we pay alice");
        var ourPayment = await node.PayInvoiceAsync(aliceInvoice.PaymentRequest, ct);
        Assert.Equal(PaymentStatus.Succeeded, ourPayment.Status);

        // LND lists a fresh private channel active before its router has the edge, so its payment pinned to it can
        // fail with insufficient_balance/no_route for a moment (NL-319): retry those
        var ourInvoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(AlicePaysSat), "n10 alice pays us", ct);
        var retryUntil = DateTime.UtcNow + s_activeTimeout;
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var alicePayment = await LndTestHelpers.SendPaymentV2Async(
                                   alice, LndTestHelpers.PinnedPayment(ourInvoice.Bolt11, [lndChannel.ChanId]), ct);
            if (alicePayment.Status == Payment.Types.PaymentStatus.Succeeded)
                break;

            Assert.True(alicePayment.FailureReason is PaymentFailureReason.FailureReasonInsufficientBalance
                                                    or PaymentFailureReason.FailureReasonNoRoute
                     && DateTime.UtcNow < retryUntil,
                        $"alice's payment failed: {alicePayment.Status} {alicePayment.FailureReason}");
            Console.WriteLine($"alice's payment failed with {alicePayment.FailureReason}; retrying");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && ours.RemoteBalance == LightningMoney.Satoshis(AliceShareSat)
                && theirs is not null && theirs.PendingHtlcs.Count == 0 && theirs.LocalBalance == AliceShareSat;
        }, s_closeTimeout, "the payments are settled on both sides", ct);
    }

    private static SignedTransaction? GetClosingTransaction(NLightningTestNode node, ChannelId channelId) =>
        node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
            ? channel.ClosingTransaction
            : null;

    private static LightningMoney WalletBalance(NLightningTestNode node)
    {
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var height = node.BlockchainMonitor.LastProcessedBlockHeight;
        return utxos.GetConfirmedBalance(height) + utxos.GetUnconfirmedBalance(height);
    }

    private static async Task<(ChannelId ChannelId, string ChannelPoint)> OpenChannelAndWaitUntilActiveAsync(
        NLightningTestNode node, LNDNodeConnection alice, CancellationToken ct)
    {
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);

        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(aliceAddress,
                                                                               LightningMoney.Satoshis(FundingSat))
        {
            FeeRatePerKw = LightningMoney.Satoshis(2_500),
            PushAmount = LightningMoney.Satoshis(PushSat)
        }, ct);
        Assert.NotNull(channel.TxId);
        var displayOrder = ((byte[])channel.TxId.Value).Reverse().ToArray();
        var channelPoint = $"{Convert.ToHexString(displayOrder).ToLowerInvariant()}:{channel.Index}";

        var deadline = DateTime.UtcNow + s_activeTimeout;
        while (true)
        {
            var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
            var ours = Assert.Single((await node.ListChannelsAsync(ct)).Channels,
                                     c => c.ChannelId == channel.ChannelId);
            if (lndChannel is { Active: true } && ours.State == ChannelState.Open && ours.IsReestablished)
                return (channel.ChannelId, channelPoint);

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Channel not active in time: LND active={lndChannel?.Active}, ours={ours.State}");

            await node.MineBlocksAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}