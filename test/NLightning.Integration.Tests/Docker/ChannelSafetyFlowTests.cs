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
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Interfaces;
using Fixtures;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using TestCollections;
using Utils;

/// <summary>
/// BOLT2 plan Proof N9 (second half, N9-T2 + N9-T4): an HTLC we offered to LND is held (hold invoice) while the peer
/// is unreachable; once the chain passes its <c>cltv_expiry + G</c> our <see cref="HtlcExpiryMonitor"/> fails the
/// channel, <see cref="ChannelFailureService"/> broadcasts our latest commitment (both signatures), it confirms, our
/// channel becomes <c>Closed</c> and LND sees a remote force close with our transaction.
/// </summary>
/// <remarks>
/// The safety services are not in the daemon's composition yet (their registration and start are an integrator
/// step), so the test builds them over the node's own service graph. LND cancels a held HTLC
/// <c>invoices.holdexpirydelta</c> (12) blocks before its expiry, so the test disconnects first: the HTLC then stays
/// in both commitments and only our deadline can resolve it.
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class ChannelSafetyFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private const ulong HoldInvoiceCltvExpiry = 24;

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
        _node = await NLightningTestNode.CreateAsync(_fixture, "safety");
        await _node.StartAsync(TestContext.Current.CancellationToken);
        (_failureService, _expiryMonitor) = StartSafetyServices(_node);
    }

    [Fact]
    public async Task Given_OfferedHtlcPastDeadline_Then_ChannelFailedAndCommitmentConfirmed()
    {
        // Arrange: a channel to alice, and our payment to her hold invoice, held
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = GetAlice();
        var channel = await OpenUsableChannelAsync(node, alice, ct);
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

            var htlc = await Poll.ForAsync(() =>
            {
                var model = node.ChannelMemoryRepository.TryGetChannel(channel.ChannelId, out var c) ? c : null;
                return model?.Commitments?.Htlcs.Values.FirstOrDefault(h => h.Direction == HtlcDirection.Outgoing
                                                                        && h.IsInCommit(Domain.Bitcoin.Transactions
                                                                           .Enums.CommitmentSide.Local)
                                                                        && h.IsInCommit(Domain.Bitcoin.Transactions
                                                                           .Enums.CommitmentSide.Remote));
            }, s_timeout, "our HTLC in both commitments", ct);
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

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["alice"]);

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
        LNDNodeConnection alice, CancellationToken ct)
    {
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(aliceAddress,
                                                                               LightningMoney.Satoshis(1_000_000))
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()})");

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(alice, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], [node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice], [node], ct);
        return channel;
    }

    private LNDNodeConnection GetAlice()
    {
        var alice = _fixture.Builder?.LNDNodePool?.ReadyNodes.First(x => x.LocalAlias == "alice");
        Assert.NotNull(alice);
        return alice;
    }

    private static async Task CancelHoldInvoiceQuietlyAsync(LNDNodeConnection alice, byte[] paymentHash)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LndTestHelpers.CancelInvoiceAsync(alice, paymentHash, timeoutCts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not cancel the hold invoice: {e.Message}");
        }
    }
}