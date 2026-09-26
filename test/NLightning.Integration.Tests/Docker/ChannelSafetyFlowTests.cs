using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Application.Channels.Safety;
using Application.Payments.Onion;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Fixtures;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using TestCollections;
using Utils;

/// <summary>
/// BOLT2 plan Proof N9 (second half, N9-T2 + N9-T4), against LND:
/// <list type="bullet">
///   <item>An HTLC we offered to LND is held (hold invoice) while the peer is unreachable; once the chain passes its
///   <c>cltv_expiry + G</c> our <see cref="HtlcExpiryMonitor"/> fails the channel, <see cref="ChannelFailureService"/>
///   broadcasts our latest commitment (both signatures), it confirms, our channel becomes <c>Closed</c> and LND sees
///   a remote force close with our transaction.</item>
///   <item>An HTLC LND Alice forwards through us to LND David's hold invoice: while David holds it, the monitor leaves
///   the incoming HTLC alone (it is continued downstream) and fails no channel; when David gives up on it
///   (<c>invoices.holdexpirydelta</c> before its expiry) we fail it back to Alice before the incoming HTLC's
///   deadlines (our fail-back height <c>cltv_expiry_in - FailBackBlocks</c> and the downstream timeout deadline
///   <c>cltv_expiry_out + G</c>), and both channels stay open.</item>
/// </list>
/// </summary>
/// <remarks>
/// The safety services are not in the daemon's composition yet (their registration and start are an integrator
/// step), so the test builds them over the node's own service graph. LND cancels a held HTLC
/// <c>invoices.holdexpirydelta</c> (12) blocks before its expiry, so the first test disconnects first: the HTLC then
/// stays in both commitments and only our deadline can resolve it. Our node runs with a <c>cltv_expiry_delta</c> of
/// <see cref="OurCltvExpiryDelta"/>, which is also its fail-back distance.
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class ChannelSafetyFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private const ulong HoldInvoiceCltvExpiry = 24;
    private const ushort OurCltvExpiryDelta = 40;
    private const uint OurFeeBaseMsat = 1_000;
    private const uint OurFeeProportionalMillionths = 100;

    /// <summary>
    /// LND's default <c>invoices.holdexpirydelta</c>: it cancels a held HTLC this many blocks before its expiry.
    /// </summary>
    private const uint LndHoldExpiryDelta = 12;

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;
    private ChannelFailureService? _failureService;
    private HtlcExpiryMonitor? _expiryMonitor;

    public ChannelSafetyFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "safety", configureNodeOptions: o =>
        {
            o.Routing.CltvExpiryDelta = OurCltvExpiryDelta;
            o.Routing.FeeBaseMsat = OurFeeBaseMsat;
            o.Routing.FeeProportionalMillionths = OurFeeProportionalMillionths;
        });
        await _node.StartAsync(TestContext.Current.CancellationToken);
        (_failureService, _expiryMonitor) = StartSafetyServices(_node);
    }

    [Fact]
    public async Task Given_OfferedHtlcPastDeadline_Then_ChannelFailedAndCommitmentConfirmed()
    {
        // Arrange: a channel to alice, and our payment to her hold invoice, held
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await OpenUsableChannelAsync(node, alice, null, ct);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(alice, paymentHash, 50_000_000, [], ct,
                                                                   "n9 offered htlc past deadline",
                                                                   HoldInvoiceCltvExpiry);
        try
        {
            var inFlight = await node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
            Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
            await LndTestHelpers.WaitForInvoiceStateAsync(alice, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          s_timeout, ct);

            var htlc = await WaitForHtlcInBothCommitmentsAsync(node, channel.ChannelId, HtlcDirection.Outgoing, ct);
            var deadline = _expiryMonitor!.Policy.OfferedDeadline(htlc.CltvExpiry);
            Console.WriteLine($"Our HTLC {htlc.Id}: cltv_expiry {htlc.CltvExpiry}, deadline {deadline}");

            // Nobody can resolve it off chain any more: drop the connection (neither side reconnects)
            node.PeerManager.DisconnectPeer(new Domain.Crypto.ValueObjects.CompactPubKey(alice.LocalNodePubKeyBytes));
            await Poll.UntilAsync(async () => !await LndTestHelpers.IsConnectedToAsync(alice, node.NodeIdHex, ct),
                                  s_timeout, "alice disconnected from us", ct);

            // Act: mine to one block before the deadline (nothing happens), then to the deadline
            var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
            if (deadline - 1 > tip)
                await ChainSync.MineAndWaitAsync(_fixture, (int)(deadline - 1 - tip), [alice], [node], ct);
            await _expiryMonitor.WhenIdleAsync();
            Assert.Equal(ChannelState.Open, (await node.GetChannelAsync(channel.ChannelId, ct)).State);

            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], [node], ct);
            var displayTxId = await Poll.ForAsync(
                                  () => _failureService!.TryGetPublishedCommitment(channel.ChannelId, out var txId)
                                            ? new uint256(txId)
                                            : null,
                                  s_timeout, "our commitment broadcast", ct);
            Console.WriteLine($"Broadcast our commitment {displayTxId}");
            Assert.Equal(ChannelState.Failed, (await node.GetChannelAsync(channel.ChannelId, ct)).State);
            var mempool = await _fixture.Bitcoin.GetRawMempoolAsync(ct);
            Assert.Contains(displayTxId, mempool);

            // The commitment confirms
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], [node], ct);

            // Assert: our channel is closed, the commitment is in a block with both signatures, and LND saw its peer
            // force close with our transaction
            await Poll.UntilAsync(async () => (await node.GetChannelAsync(channel.ChannelId, ct)).State
                                           == ChannelState.Closed, s_timeout, "our channel closed", ct);

            var confirmed = await _fixture.Bitcoin.GetRawTransactionInfoAsync(displayTxId, ct);
            Assert.True(confirmed.Confirmations >= 1);
            var tx = confirmed.Transaction;
            Assert.Equal(4, tx.Inputs[0].WitScript.PushCount);
            Assert.Contains(tx.Outputs, o => o.Value == Money.Satoshis(50_000));
            Assert.Equal(channel.ChannelPoint(),
                         $"{tx.Inputs[0].PrevOut.Hash}:{tx.Inputs[0].PrevOut.N}");

            await Poll.UntilAsync(async () =>
            {
                var closed = await alice.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                            cancellationToken: ct);
                var ours = closed.Channels.FirstOrDefault(c => c.ChannelPoint == channel.ChannelPoint());
                if (ours is not null)
                {
                    Console.WriteLine($"LND closed the channel: {ours.CloseType}, closing tx {ours.ClosingTxHash}");
                    Assert.Equal(ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ours.CloseType);
                    Assert.Equal(displayTxId.ToString(), ours.ClosingTxHash);
                    return true;
                }

                // LND may want another block before it records the close
                await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], [node], ct);
                return false;
            }, s_timeout, "LND lists the channel as remote force closed", ct);
        }
        finally
        {
            await CancelHoldInvoiceQuietlyAsync(alice, paymentHash);
        }
    }

    [Fact]
    public async Task Given_ForwardedHtlcHeldDownstream_When_DownstreamGivesUp_Then_FailedBackUpstreamBeforeDeadline()
    {
        // Arrange: alice -> us -> david, each channel funded by us (alice gets a push so she can pay through us)
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        var david = _fixture.GetLndNode("david");
        var monitor = _expiryMonitor!;

        // Two coins, so the second open never waits for the first one's change
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        var upstream = await OpenUsableChannelAsync(node, alice, LightningMoney.Satoshis(500_000), ct);
        var downstream = await OpenUsableChannelAsync(node, david, null, ct);
        var upstreamLnd = await LndTestHelpers.GetChannelByPointAsync(alice, upstream.ChannelPoint(), ct);
        Assert.NotNull(upstreamLnd);
        var downstreamScid = (await node.GetChannelAsync(downstream.ChannelId, ct)).ShortChannelId;
        Assert.NotNull(downstreamScid);

        // David's hold invoice, reachable only through us (our private channel, our policy)
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(node.NodeIdHex, downstreamScid.Value.ToUInt64(),
                                                                   OurFeeBaseMsat, OurFeeProportionalMillionths,
                                                                   OurCltvExpiryDelta));
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [hint], ct,
                                                                   "n9 forwarded htlc failed back",
                                                                   HoldInvoiceCltvExpiry);
        try
        {
            // Alice pays over her channel with us; the payment stays in flight while david holds it
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var payment = LndTestHelpers.SendPaymentV2Async(
                alice, LndTestHelpers.PinnedPayment(holdInvoice.PaymentRequest, [upstreamLnd.ChanId]), ct,
                TimeSpan.FromMinutes(5));
            await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          s_timeout, ct);

            var incoming = await WaitForHtlcInBothCommitmentsAsync(node, upstream.ChannelId, HtlcDirection.Incoming,
                                                                   ct);
            var outgoing = await WaitForHtlcInBothCommitmentsAsync(node, downstream.ChannelId,
                                                                   HtlcDirection.Outgoing, ct);
            Assert.Equal(incoming.PaymentHash, outgoing.PaymentHash);
            Assert.Equal((uint)OurCltvExpiryDelta, incoming.CltvExpiry - outgoing.CltvExpiry);

            var failBackHeight = monitor.Policy.FailBackHeight(incoming.CltvExpiry);
            var downstreamDeadline = monitor.Policy.OfferedDeadline(outgoing.CltvExpiry);
            var davidGivesUpAt = outgoing.CltvExpiry - LndHoldExpiryDelta;
            Console.WriteLine($"Incoming HTLC {incoming.Id}: cltv_expiry {incoming.CltvExpiry}, fail-back height "
                            + $"{failBackHeight}; outgoing HTLC {outgoing.Id}: cltv_expiry {outgoing.CltvExpiry}, "
                            + $"deadline {downstreamDeadline}; david gives up at {davidGivesUpAt}");
            Assert.True(davidGivesUpAt < failBackHeight && davidGivesUpAt < downstreamDeadline);

            // The forward is recorded: the monitor treats the incoming HTLC as continued downstream
            Assert.Equal(ForwardCircuitStatus.Offered,
                         (await GetCircuitAsync(node, upstream.ChannelId, incoming.Id))?.Status);

            // Act: mine one block at a time until alice's payment fails (david gives up on the held HTLC and we fail
            // it back), never up to the incoming HTLC's deadlines
            uint failedAt = 0;
            while (!payment.IsCompleted)
            {
                var height = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
                Assert.True(height + 1 < failBackHeight && height + 1 < downstreamDeadline,
                            $"alice's payment still in flight at height {height}");
                await ChainSync.MineAndWaitAsync(_fixture, 1, [alice, david], [node], ct);
                await monitor.WhenIdleAsync();

                // Nothing is failed by the monitor and the channels stay open
                AssertChannelNotFailed(upstream.ChannelId);
                AssertChannelNotFailed(downstream.ChannelId);

                if (await WaitUntilCompletedAsync(payment, TimeSpan.FromSeconds(3), ct))
                    failedAt = height + 1;
            }

            // Assert: alice's payment failed at david (not at us), after david gave up and before the incoming
            // HTLC's deadlines
            var result = await payment;
            Console.WriteLine($"Alice's payment {result.Status} ({result.FailureReason}) at height {failedAt}");
            Assert.Equal(Payment.Types.PaymentStatus.Failed, result.Status);
            Assert.Equal(PaymentFailureReason.FailureReasonIncorrectPaymentDetails, result.FailureReason);
            var attempt = Assert.Single(result.Htlcs);
            Assert.Equal(2u, attempt.Failure.FailureSourceIndex);
            Assert.True(failedAt >= davidGivesUpAt, $"failed at {failedAt}, before david gave up at {davidGivesUpAt}");
            Assert.True(failedAt < failBackHeight && failedAt < downstreamDeadline);

            // Both HTLCs leave both commitments, the circuit is failed, and both channels are still usable
            await Poll.UntilAsync(() => Task.FromResult(HtlcCount(node, upstream.ChannelId) == 0
                                                     && HtlcCount(node, downstream.ChannelId) == 0),
                                  s_timeout, "both HTLCs removed from both commitments", ct);
            var circuit = await GetCircuitAsync(node, upstream.ChannelId, incoming.Id);
            Assert.True(circuit is null or { Status: ForwardCircuitStatus.Failed }, $"circuit {circuit?.Status}");
            AssertChannelNotFailed(upstream.ChannelId);
            AssertChannelNotFailed(downstream.ChannelId);
            Assert.True((await node.GetChannelAsync(upstream.ChannelId, ct)).IsUsable());
            Assert.True((await node.GetChannelAsync(downstream.ChannelId, ct)).IsUsable());
            var aliceChannel = await LndTestHelpers.GetChannelByPointAsync(alice, upstream.ChannelPoint(), ct);
            Assert.NotNull(aliceChannel);
            Assert.Empty(aliceChannel.PendingHtlcs);
            Assert.True((await LndTestHelpers.GetChannelByPointAsync(david, downstream.ChannelPoint(), ct))?.Active);
        }
        finally
        {
            await CancelHoldInvoiceQuietlyAsync(david, paymentHash);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);
        }

        if (_expiryMonitor is not null)
            await _expiryMonitor.StopAsync();
        _failureService?.Stop();
        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Builds the N9 safety services over the node's service graph and starts them, as the daemon will once they are
    /// registered (<c>AddChannelSafetyServices</c>).
    /// </summary>
    private static (ChannelFailureService, HtlcExpiryMonitor) StartSafetyServices(NLightningTestNode node)
    {
        var services = node.Services;
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var nodeOptions = services.GetRequiredService<IOptions<NodeOptions>>();
        var builder = new LocalCommitmentBroadcastBuilder(
            services.GetRequiredService<ICommitmentTransactionModelFactory>(),
            services.GetRequiredService<ICommitmentTransactionBuilder>(),
            services.GetRequiredService<ILightningSigner>());
        var failureService = new ChannelFailureService(
            services.GetRequiredService<IBlockchainMonitor>(),
            new PeerChannelErrorSender(loggerFactory.CreateLogger<PeerChannelErrorSender>(), services),
            services.GetRequiredService<IChannelLockProvider>(), services.GetRequiredService<IChannelMemoryRepository>(),
            builder, services.GetRequiredService<ILightningSigner>(),
            loggerFactory.CreateLogger<ChannelFailureService>(), services.GetRequiredService<IServiceScopeFactory>(),
            services, nodeOptions);
        var monitor = new HtlcExpiryMonitor(
            services.GetRequiredService<IBlockchainMonitor>(), failureService,
            services.GetRequiredService<IChannelMemoryRepository>(), services.GetRequiredService<IChannelOperations>(),
            services.GetRequiredService<IFailureOnionService>(), loggerFactory.CreateLogger<HtlcExpiryMonitor>(),
            nodeOptions, services.GetRequiredService<IServiceScopeFactory>(),
            incomingOnionProcessor: services.GetRequiredService<IncomingOnionProcessor>());

        failureService.Start();
        monitor.Start();
        return (failureService, monitor);
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

            // LND may want more confirmations than we do
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

    private static int HtlcCount(NLightningTestNode node, ChannelId channelId) =>
        node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel)
            ? channel.Commitments?.Htlcs.Count ?? 0
            : 0;

    private static async Task<ForwardCircuitModel?> GetCircuitAsync(NLightningTestNode node, ChannelId channelId,
                                                                    ulong htlcId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ForwardCircuitDbRepository
                          .GetByIncomingAsync(channelId, htlcId);
    }

    private void AssertChannelNotFailed(ChannelId channelId)
    {
        Assert.False(_failureService!.TryGetPublishedCommitment(channelId, out _),
                     $"the monitor failed channel {channelId}");
        Assert.True(_node!.ChannelMemoryRepository.TryGetChannel(channelId, out var channel)
                 && channel.State == ChannelState.Open, $"channel {channelId} is not open");
    }

    private static async Task<bool> WaitUntilCompletedAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        await Task.WhenAny(task, Task.Delay(timeout, ct));
        return task.IsCompleted;
    }

    private static async Task CancelHoldInvoiceQuietlyAsync(LNDNodeConnection node, byte[] paymentHash)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LndTestHelpers.CancelInvoiceAsync(node, paymentHash, timeoutCts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not cancel the hold invoice: {e.Message}");
        }
    }
}