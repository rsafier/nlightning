using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker;

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
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// Proof N10 (BOLT2 plan): cooperative close against LND. A channel we funded (with a push to alice) is closed by us
/// and by alice, with and without <c>fee_range</c>; the agreed closing transaction confirms, LND lists a
/// <c>COOPERATIVE_CLOSE</c> with the same txid, our side becomes Closed after 6 blocks and our wallet receives our
/// output.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class CooperativeCloseFlowTests : IAsyncLifetime
{
    private const long FundingSat = 1_000_000;
    private const long PushSat = 200_000;

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
        await AssertClosedAsync(node, alice, channelId, channelPoint, closingTx, walletBefore, Initiator.Local, ct);
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
        var walletBefore = WalletBalance(node);

        // Act: alice closes (the stream reports the pending close; the close goes on without it)
        var parts = channelPoint.Split(':');
        using var closeCall = alice.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint
            {
                FundingTxidStr = parts[0],
                OutputIndex = uint.Parse(parts[1])
            }
        }, cancellationToken: ct);
        var pending = await Poll.ForAsync(async () =>
        {
            if (!await closeCall.ResponseStream.MoveNext(ct))
                return null;
            return closeCall.ResponseStream.Current.ClosePending;
        }, s_closeTimeout, "alice reports the close pending", ct);

        // Assert
        var closingTx = await Poll.ForAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.Single(c => c.ChannelId == channelId);
            return ours.State == ChannelState.Closing ? GetClosingTransaction(node, channelId) : null;
        }, s_closeTimeout, "our channel is Closing", ct);
        // LND's ClosePending txid is in internal byte order, like ours
        Assert.Equal(Convert.ToHexString(pending.Txid.ToByteArray()).ToLowerInvariant(),
                     Convert.ToHexString((byte[])closingTx.TxId).ToLowerInvariant());
        await AssertClosedAsync(node, alice, channelId, channelPoint, closingTx, walletBefore, Initiator.Remote, ct);
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
    /// Mines 6 blocks, then: LND lists a cooperative close with our closing txid and the right initiator, alice got
    /// the push back, our channel is Closed, and our wallet received our output (funding - push - fee).
    /// </summary>
    private static async Task AssertClosedAsync(NLightningTestNode node, LNDNodeConnection alice, ChannelId channelId,
                                                string channelPoint, SignedTransaction closingTx,
                                                LightningMoney walletBefore, Initiator initiator,
                                                CancellationToken ct)
    {
        var closingTxHex = Convert.ToHexString(((byte[])closingTx.TxId).Reverse().ToArray()).ToLowerInvariant();
        var tx = NBitcoin.Transaction.Load(closingTx.RawTxBytes, Network.RegTest);
        var fee = FundingSat - tx.Outputs.Sum(o => o.Value.Satoshi);
        var ourOutput = FundingSat - PushSat - fee;
        Assert.InRange(fee, 1, 20_000);
        Assert.Contains(tx.Outputs, o => o.Value.Satoshi == PushSat);
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
        Assert.Equal(PushSat, summary.SettledBalance);

        await Poll.UntilAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.Single(c => c.ChannelId == channelId);
            return ours.State == ChannelState.Closed;
        }, s_closeTimeout, "our channel is Closed", ct);

        await Poll.UntilAsync(() => Task.FromResult(WalletBalance(node) - walletBefore
                                                 == LightningMoney.Satoshis(ourOutput)),
                              s_closeTimeout, "our wallet received our closing output", ct);
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