using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Cluster.Live;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>Live policy-validating Rust VLS signer interoperability against an LND regtest peer.</summary>
[Trait("Category", "Cluster")]
public class VlsSignerLndClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 1_000_000;
    private const long PushMsat = 300_000_000;
    // Fractional satoshis on purpose: VLS's default policy (enforce_balance off) takes millisatoshi amounts
    private const long ToPeerMsat = 200_000_123;
    private const long ToUsMsat = 50_000_777;
    private const uint FeeBaseMsat = 1_001;
    private const uint FeeProportionalMillionths = 1_234;

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// bitcoind + LND + our node: the topology opens our v1 channel to LND without a push; an approved
    /// payment establishes peer liquidity; we pay in both directions, restart the injected signer and node with their signing state and database,
    /// reestablish with LND, pay in both directions again, and confirm a cooperative close.
    /// </summary>
    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_AnInjectedVlsSignerAndLnd_When_WePayBothWaysRestartBothAndClose_Then_BothEndsAgree(bool forceClose)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(Options("vls-signer-lnd"), ct);
        var ns = run.Namespace;
        try
        {
            await using var signer = new NLightning.RemoteSigning.Tests.VlsGatewayFixture();
            await signer.InitializeAsync(ct);
            var connection = new NLightning.Infrastructure.VlsSigning.VlsSignerConnection(new NLightning.Infrastructure.VlsSigning.VlsSignerOptions
            { SocketPath = signer.SocketPath, TokenFile = signer.TokenFile, Network = "regtest" });
            var keys = new NLightning.Infrastructure.VlsSigning.VlsSecureKeyManager(connection);
            await using var inProcess = new InProcessNodeDeployer
            {
                KeyManager = _ => keys,
                PodFacingHost = Environment.GetEnvironmentVariable("NLTG_ADOPT_NAMESPACE") == "1"
                    ? System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                            .First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString()
                    : null,
                ConfigureNodeOptions = (_, options) =>
                {
                    ConservativeFeatures(options.Features);
                    options.MaxDustHtlcExposureMsat = 0;
                    options.HtlcMinimumAmount = NLightning.Domain.Money.LightningMoney.Satoshis(1_000);
                    options.Routing.FeeBaseMsat = FeeBaseMsat;
                    options.Routing.FeeProportionalMillionths = FeeProportionalMillionths;
                },
                ConfigureNode = node =>
                {
                    node.ConfigureServices = services => services.AddLogging(builder =>
                        builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
                    node.VlsSignerConnection = connection;
                    node.ExtraConfiguration["Signing:Mode"] = "Vls";
                    node.ExtraConfiguration["Signing:SocketPath"] = signer.SocketPath;
                    node.ExtraConfiguration["Signing:AuthTokenFile"] = signer.TokenFile;
                }
            };
            using var topology = await Builder(inProcess)
                                      .AddLnd("lnd")
                                      .FundWallet("lnd", WalletSat)
                                      .AddChannel("nltg", "lnd", CapacitySat)
                                      .BuildAsync(run, ct);
            var built = watch.Elapsed;
            var nltg = topology.InProcessNode("nltg");
            var lnd = topology.Node<LndNode>("lnd");
            var channel = Assert.Single(topology.Channels);
            var fundingTxId = channel.Open.FundingTxId;
            // Establish peer liquidity through an explicitly approved payment; VLS refuses outbound pushes.
            var (_, liquidity, _) = await PayApprovedAsync(signer, nltg, lnd, PushMsat, s_stepTimeout, ct);
            Assert.True(liquidity.Succeeded, liquidity.FailureReason);

            // Act: pay both ways (LND may need a retry while its router learns the fresh channel, NL-319)
            var (_, toLnd, toLndAttempts) = await PayApprovedAsync(signer, nltg, lnd, ToPeerMsat, s_stepTimeout, ct);
            var (_, toUs, toUsAttempts) = await PayApprovedAsync(signer, lnd, nltg, ToUsMsat, s_stepTimeout, ct);
            var paid = watch.Elapsed;

            // Act: restart the signer and our node; it redials LND (stored as lnd.<ns>.svc.cluster.local) and reestablishes
            var restart = Stopwatch.StartNew();
            var identity = await nltg.GetNodeIdAsync(ct);
            await nltg.TestNode.StopAsync();
            await signer.RestartAsync(ct);
            await nltg.TestNode.StartAsync(ct);
            Assert.Equal(identity, await nltg.GetNodeIdAsync(ct));
            Assert.IsType<NLightning.Infrastructure.VlsSigning.VlsLightningSigner>(
                nltg.TestNode.Services.GetRequiredService<NLightning.Domain.Bitcoin.Interfaces.ILightningSigner>());
            Assert.Throws<NotSupportedException>(() => keys.GetNodeKeyPair());
            Assert.False(File.Exists(Path.Combine(signer.DirectoryPath, "node.key")));
            await topology.WaitChannelsActiveAsync(s_stepTimeout, ct);
            var restartedIn = restart.Elapsed;
            var (_, afterRestart, _) = await PayApprovedAsync(signer, lnd, nltg, ToUsMsat, s_stepTimeout, ct);
            var (_, outgoingAfterRestart, _) = await PayApprovedAsync(signer, nltg, lnd, ToPeerMsat, s_stepTimeout, ct);

            // Assert: v1, approved peer liquidity, every payment settled, and the balances moved
            var ourChannel = await nltg.FindChannelAsync(fundingTxId, ct);
            Assert.NotNull(ourChannel);
            Assert.Equal(ChannelVersion.V1, ChannelVersionOf(nltg, ourChannel.ChannelId));
            Assert.True(toLnd.Succeeded, toLnd.FailureReason);
            Assert.True(toUs.Succeeded, toUs.FailureReason);
            Assert.True(afterRestart.Succeeded, afterRestart.FailureReason);
            Assert.True(outgoingAfterRestart.Succeeded, outgoingAfterRestart.FailureReason);
            await WaitLocalBalanceAsync(nltg, fundingTxId, CapacitySat * 1000 - PushMsat - 2 * ToPeerMsat + 2 * ToUsMsat, ct);
            // LND's ListChannels reports whole satoshis (local_balance): its side matches to the satoshi
            await WaitLocalBalanceAsync(lnd, fundingTxId, (PushMsat + 2 * ToPeerMsat - 2 * ToUsMsat) / 1000 * 1000, ct);
            Assert.NotNull(channel.ShortChannelId);
            Assert.Equal(channel.ShortChannelId, ourChannel.ShortChannelId?.ToString());

            // Act: use the production close command and require Bitcoin Core to accept its signature.
            string closingTxId;
            if (forceClose)
            {
                using var scope = nltg.TestNode.Services.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<NLightning.Daemon.Interfaces.IClientCommandHandler<
                    NLightning.Domain.Client.Requests.ForceCloseChannelClientRequest,
                    NLightning.Domain.Client.Responses.ForceCloseChannelClientResponse>>();
                var response = await handler.HandleAsync(
                    new NLightning.Domain.Client.Requests.ForceCloseChannelClientRequest(ourChannel.ChannelId), ct);
                Assert.Equal("Broadcast", response.Status);
                Assert.NotNull(response.CommitmentTxId);
                closingTxId = new NBitcoin.uint256((byte[])response.CommitmentTxId.Value).ToString();
            }
            else
            {
                closingTxId = await nltg.CloseChannelAsync(ourChannel.ChannelId, ct);
                // Let the peer finish cooperative negotiation before mining; its confirmation subscription
                // otherwise races a batch of blocks mined immediately after our earlier broadcast.
                await ClusterPoll.UntilDoneAsync(async c =>
                {
                    var pending = await lnd.Lightning.PendingChannelsAsync(new PendingChannelsRequest(), cancellationToken: c);
                    return pending.WaitingCloseChannels.Any(x => x.ClosingTxid == closingTxId)
                        ? null : "LND has not registered the exact cooperative closing transaction";
                }, s_stepTimeout, "LND registered cooperative close before mining", ct);
            }
            Assert.NotNull(channel.Open.OutputIndex);
            await MineClosingAsync(topology, closingTxId, fundingTxId, channel.Open.OutputIndex.Value, ct);

            if (forceClose)
            {
                // This acceptance gate proves valid commitment broadcast and confirmation, not full sweep recovery.
                Log($"{ns}: VLS force-close commitment {closingTxId} accepted and confirmed by Bitcoin Core");
                return;
            }

            // Assert: cooperative close agrees on both ends.
            await ClusterPoll.UntilDoneAsync(async c =>
            {
                var ours = await nltg.FindChannelAsync(fundingTxId, c);
                return ours is null || ours.State == ChannelState.Closed ? null : $"ours {ours.State}";
            }, s_stepTimeout, "our channel closed", ct);
            var closed = await ClusterPoll.ForAsync(async c =>
            {
                var response = await lnd.Lightning.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                       cancellationToken: c).ResponseAsync;
                return response.Channels.FirstOrDefault(x => x.ChannelPoint.StartsWith(fundingTxId,
                                                                                        StringComparison.Ordinal));
            }, s_stepTimeout, TimeSpan.FromMilliseconds(500), "LND lists the channel closed", ct);
            Assert.Equal(closingTxId, closed.ClosingTxHash);
            Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, closed.CloseType);

            Log($"{ns}: built in {built.TotalSeconds:F1} s (v1 channel {channel.ShortChannelId}), paid LND in "
              + $"{toLndAttempts} and LND paid us in {toUsAttempts} attempt(s) by {paid.TotalSeconds:F1} s, restart to "
              + $"channel active {restartedIn.TotalSeconds:F1} s, closed by {closingTxId} at "
              + $"{watch.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            await DisposeRunAsync(run, watch);
        }
    }

    [Fact(Explicit = true)]
    public async Task Given_TwoLndChannels_When_PaymentRoutesThroughVlsNode_Then_RealForwardCircuitSettles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var signer = new NLightning.RemoteSigning.Tests.VlsGatewayFixture();
        await signer.InitializeAsync(ct);
        var connection = new NLightning.Infrastructure.VlsSigning.VlsSignerConnection(
            new NLightning.Infrastructure.VlsSigning.VlsSignerOptions
            { SocketPath = signer.SocketPath, TokenFile = signer.TokenFile });
        var keys = new NLightning.Infrastructure.VlsSigning.VlsSecureKeyManager(connection);
        await using var run = await TestRun.StartAsync(Options("vls-forward-lnd"), ct);
        await using var deployer = new InProcessNodeDeployer
        {
            KeyManager = _ => keys,
            PodFacingHost = Environment.GetEnvironmentVariable("NLTG_ADOPT_NAMESPACE") == "1"
                ? System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                    .First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString() : null,
            ConfigureNodeOptions = (_, options) =>
                {
                    ConservativeFeatures(options.Features);
                    options.MaxDustHtlcExposureMsat = 0;
                    options.HtlcMinimumAmount = NLightning.Domain.Money.LightningMoney.Satoshis(1_000);
                    options.Routing.FeeBaseMsat = FeeBaseMsat;
                    options.Routing.FeeProportionalMillionths = FeeProportionalMillionths;
                },
            ConfigureNode = node =>
            {
                node.ConfigureServices = services => services.AddLogging(builder =>
                    builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
                node.VlsSignerConnection = connection;
                node.ExtraConfiguration["Signing:Mode"] = "Vls";
                node.ExtraConfiguration["Signing:SocketPath"] = signer.SocketPath;
                node.ExtraConfiguration["Signing:AuthTokenFile"] = signer.TokenFile;
            }
        };
        using var topology = await new TopologyBuilder
        { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_stepTimeout }
            .AddBitcoinCore("miner").AddLnd("alice").AddNLightning("nltg").AddLnd("carol")
            .UseInProcessNodes(deployer).FundWallet("alice", WalletSat).FundWallet("nltg", WalletSat)
            .AddChannel("alice", "nltg", CapacitySat, PushMsat)
            .AddChannel("nltg", "carol", CapacitySat).BuildAsync(run, ct);
        var alice = topology.Node<LndNode>("alice");
        var carol = topology.Node<LndNode>("carol");
        var node = topology.InProcessNode("nltg");
        // LND omits automatic private hints for an unannounced intermediary. Supply the actual channel
        // and production forwarding policy explicitly in Carol's signed BOLT11 invoice.
        var intermediary = await node.GetNodeIdAsync(ct);
        var carolChannels = await carol.Lightning.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: ct);
        var toCarol = Assert.Single(carolChannels.Channels, x => x.RemotePubkey == intermediary && x.Active);
        var policy = node.TestNode.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<
            NLightning.Domain.Node.Options.NodeOptions>>().Value.Routing;
        Assert.Equal(FeeBaseMsat, policy.FeeBaseMsat);
        Assert.Equal(FeeProportionalMillionths, policy.FeeProportionalMillionths);
        var hint = new RouteHint();
        hint.HopHints.Add(new HopHint
        {
            NodeId = intermediary,
            ChanId = toCarol.ChanId,
            FeeBaseMsat = policy.FeeBaseMsat,
            FeeProportionalMillionths = policy.FeeProportionalMillionths,
            CltvExpiryDelta = policy.CltvExpiryDelta
        });
        var request = new Invoice { ValueMsat = ToPeerMsat, Memo = "LND through actual VLS", Private = true };
        request.RouteHints.Add(hint);
        var created = await carol.Lightning.AddInvoiceAsync(request, cancellationToken: ct);
        var invoice = new TestInvoice(created.PaymentRequest, Convert.ToHexString(created.RHash.ToByteArray()).ToLowerInvariant());
        var decoded = await alice.Lightning.DecodePayReqAsync(new PayReqString { PayReq = invoice.Bolt11 }, cancellationToken: ct);
        Assert.Contains(decoded.RouteHints, x => x.HopHints.Any(h =>
            h.NodeId == intermediary && h.ChanId == toCarol.ChanId));
        new NLightning.Infrastructure.VlsSigning.VlsPaymentApprovalClient(signer.ApprovalSocketPath,
            signer.ApprovalTokenFile).AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11);
        TestPaymentResult? payment = null;
        var deadline = DateTime.UtcNow + s_stepTimeout;
        while (DateTime.UtcNow < deadline)
        {
            payment = await alice.PayInvoiceAsync(invoice.Bolt11, ct);
            if (payment.Succeeded) break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        Assert.NotNull(payment);
        Assert.True(payment.Succeeded, payment.FailureReason);
        await ClusterPoll.UntilDoneAsync(async c =>
        {
            using var scope = node.TestNode.Services.CreateScope();
            var unit = scope.ServiceProvider.GetRequiredService<NLightning.Domain.Persistence.Interfaces.IUnitOfWork>();
            var circuits = await unit.ForwardCircuitDbRepository.ListAsync(
                new NLightning.Domain.Payments.Models.ForwardCircuitListQuery(0, 100), c);
            var settled = circuits.Where(x => x.PaymentHash.ToString() == invoice.PaymentHashHex
                && x.Status == NLightning.Domain.Payments.Enums.ForwardCircuitStatus.Fulfilled).ToList();
            if (settled.Count == 0) return "waiting for persisted fulfilled VLS forward";
            Assert.All(settled, circuit =>
            {
                Assert.NotNull(circuit.OutgoingChannelId);
                Assert.NotEqual(circuit.IncomingChannelId, circuit.OutgoingChannelId.Value);
                Assert.Equal((ulong)ToPeerMsat, circuit.OutgoingAmount.MilliSatoshi);
                // The fractional forwarding fee (base + proportional) reaches the incoming HTLC exactly
                Assert.Equal(FeeBaseMsat + (ulong)ToPeerMsat * FeeProportionalMillionths / 1_000_000,
                             circuit.ActualFee.MilliSatoshi);
            });
            return null;
        }, s_stepTimeout, "VLS node persisted successful forwarding", ct);
        Assert.IsType<NLightning.Infrastructure.VlsSigning.VlsLightningSigner>(
            node.TestNode.Services.GetRequiredService<NLightning.Domain.Bitcoin.Interfaces.ILightningSigner>());
        Log($"{run.Namespace}: LND -> VLS NLightning -> LND settled {ToPeerMsat} msat");
    }

    private static void ConservativeFeatures(NLightning.Domain.Node.Options.FeatureOptions features)
    {
        features.OptionAnchors = features.DualFund = features.OptionQuiesce = features.OptionSplice =
            features.OptionSimpleTaproot = features.OptionGossipV2 = features.OptionSimpleClose =
            features.OptionRouteBlinding = features.OptionOnionMessages = features.OptionTrampolineRouting =
            features.OptionProvideStorage = features.BeyondSegwitShutdown = features.ZeroConf =
            NLightning.Domain.Enums.FeatureSupport.No;
    }

    private static async Task<(TestInvoice Invoice, TestPaymentResult Result, int Attempts)> PayApprovedAsync(
        NLightning.RemoteSigning.Tests.VlsGatewayFixture signer, ILightningTestPeer payer, ILightningTestPeer payee,
        long amountMsat, TimeSpan timeout, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(amountMsat, "VLS live acceptance", ct);
        if (payer is InProcessNode)
            new NLightning.Infrastructure.VlsSigning.VlsPaymentApprovalClient(signer.ApprovalSocketPath,
                signer.ApprovalTokenFile).AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11);
        var deadline = DateTime.UtcNow + timeout;
        var attempts = 0;
        while (true)
        {
            attempts++;
            var result = await payer.PayInvoiceAsync(invoice.Bolt11, ct);
            if (result.Succeeded || DateTime.UtcNow >= deadline)
                return (invoice, result, attempts);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with { Quota = NamespaceQuota.Spike, Log = Log };

    /// <summary>bitcoind <c>miner</c> and our funded node <c>nltg</c>; the test adds the pod peer.</summary>
    private static TopologyBuilder Builder(InProcessNodeDeployer inProcess) =>
        new TopologyBuilder { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_stepTimeout }
           .AddBitcoinCore("miner")
           .AddNLightning("nltg")
           .UseInProcessNodes(inProcess)
           .FundWallet("nltg", WalletSat);

    /// <summary>
    /// Waits until <paramref name="node"/>'s side of the channel holds <paramref name="expectedMsat"/>: a payer has the
    /// preimage before the commitment dance that moves the balances is over.
    /// </summary>
    private static Task<TestChannel> WaitLocalBalanceAsync(ILightningTestPeer node, string fundingTxId,
                                                           long expectedMsat, CancellationToken ct) =>
        ClusterPoll.ForAsync(async c =>
        {
            var channel = (await node.ListChannelsAsync(c)).FirstOrDefault(x => x.FundingTxId == fundingTxId);
            return channel?.LocalBalanceMsat == expectedMsat ? channel : null;
        }, s_stepTimeout, TimeSpan.FromMilliseconds(250), $"{node.Alias}'s balance {expectedMsat} msat", ct);

    /// <summary>Requires the exact funding spend in Core's mempool and confirms that transaction 6 deep.</summary>
    private static async Task MineClosingAsync(TestTopology topology, string closingTxId, string fundingTxId,
                                               int fundingOutputIndex, CancellationToken ct)
    {
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        await chain.Chain.WaitForMempoolAsync(closingTxId, ct, s_stepTimeout);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        var confirmed = await chain.Chain.WaitForConfirmationAsync(closingTxId,
            TopologyDeployer.ConfirmationBlocks, ct, s_stepTimeout);
        Assert.True(confirmed.Confirmations >= TopologyDeployer.ConfirmationBlocks);
        var transaction = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = closingTxId, ["verbose"] = true }, ct);
        Assert.Equal(closingTxId, (string?)transaction["txid"]);
        var input = Assert.Single(transaction["vin"]!);
        Assert.Equal(fundingTxId, (string?)input["txid"]);
        Assert.Equal(fundingOutputIndex, (int?)input["vout"]);
    }

    private static ChannelVersion ChannelVersionOf(InProcessNode node, ChannelId channelId) =>
        node.TestNode.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
            ? channel.Version
            : throw new InvalidOperationException($"{node.Alias} has no channel {channelId} in memory");

    private static async Task DisposeRunAsync(TestRun run, Stopwatch watch)
    {
        var ns = run.Namespace;
        watch.Restart();
        await run.DisposeAsync();
        Log($"{ns}: disposed in {watch.Elapsed.TotalSeconds:F1} s (the namespace terminates in the background)");
    }
}