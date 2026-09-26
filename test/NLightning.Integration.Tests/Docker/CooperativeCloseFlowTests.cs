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
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
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

        var ourInvoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(AlicePaysSat), "n10 alice pays us", ct);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var alicePayment = await LndTestHelpers.SendPaymentV2Async(
                               alice, LndTestHelpers.PinnedPayment(ourInvoice.Bolt11, [lndChannel.ChanId]), ct);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, alicePayment.Status);

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