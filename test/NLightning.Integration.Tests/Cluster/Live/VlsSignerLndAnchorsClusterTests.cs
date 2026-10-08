using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NLightning.Testing.Lnd.Lnrpc;
using Op = NBitcoin.Op;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Cluster.Live;

using Daemon.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Infrastructure.VlsSigning;
using RemoteSigning.Tests;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// Zero-fee-HTLC anchors channels in VLS mode against LND (anchors by default): the VLS policy signer sets the channel
/// up as <c>AnchorsZeroFeeHtlc</c>, signs and validates every commitment and HTLC signature, signs our commitment for
/// the force close and the CPFP child through our anchor (VLS <c>sign_holder_anchor_input</c>) with its wallet fee input
/// (VLS onchain policy), and Bitcoin Core mines the commitment only through that child.
/// </summary>
[Trait("Category", "Cluster")]
public class VlsSignerLndAnchorsClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 1_000_000;
    private const long LiquidityMsat = 300_000_123;
    private const long ToUsMsat = 50_000_777;
    private const long ToPeerMsat = 20_000_999;

    // Our opener's lowest feerate (about 4 sat/vB) under the test node's fixed 10 sat/vB estimate: the commitment needs
    // a child
    private static readonly LightningMoney s_commitmentFeeRatePerKw = LightningMoney.Satoshis(1_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_AVlsSignedAnchorsChannelToLnd_When_WePayBothWaysAndForceClose_Then_OurAnchorChildConfirmsTheCommitment()
    {
        // Arrange: our VLS node and LND, both funded (LND keeps an on-chain reserve for anchors channels)
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await using var run = await TestRun.StartAsync(
            TestRunOptions.FromEnvironment("vls-anchors-lnd") with { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        await using var signer = new VlsGatewayFixture();
        await signer.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = signer.SocketPath, TokenFile = signer.TokenFile, Network = "regtest" });
        var keys = new VlsSecureKeyManager(connection);
        await using var deployer = new InProcessNodeDeployer
        {
            KeyManager = _ => keys,
            PodFacingHost = Environment.GetEnvironmentVariable("NLTG_ADOPT_NAMESPACE") == "1"
                ? System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                        .First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString()
                : null,
            ConfigureNodeOptions = (_, options) =>
            {
                var features = options.Features;
                features.DualFund = features.OptionQuiesce = features.OptionSplice = features.OptionSimpleTaproot =
                    features.OptionGossipV2 = features.OptionSimpleClose = features.OptionRouteBlinding =
                    features.OptionOnionMessages = features.OptionTrampolineRouting = features.OptionProvideStorage =
                    features.BeyondSegwitShutdown = features.ZeroConf = FeatureSupport.No;
                features.OptionAnchors = FeatureSupport.Optional;
                options.MaxDustHtlcExposureMsat = 0;
                options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
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
            .AddBitcoinCore("miner").AddNLightning("nltg").AddLnd("lnd")
            .UseInProcessNodes(deployer).FundWallet("nltg", WalletSat).FundWallet("lnd", WalletSat)
            .BuildAsync(run, ct);
        var nltg = topology.InProcessNode("nltg");
        var lnd = topology.Node<LndNode>("lnd");
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        Assert.IsType<VlsLightningSigner>(nltg.TestNode.Services.GetRequiredService<ILightningSigner>());

        // Act: open an anchors channel at our lowest commitment feerate, confirmed until both ends use it
        var lndId = await lnd.GetNodeIdAsync(ct);
        await nltg.ConnectAsync(await lnd.GetAddressAsync(ct), ct);
        var opened = await nltg.TestNode.OpenChannelAsync(
            new OpenChannelClientRequest(lndId, LightningMoney.Satoshis(CapacitySat))
            { FeeRatePerKw = s_commitmentFeeRatePerKw }, ct);
        var channelId = opened.ChannelId;
        var model = nltg.TestNode.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var c)
            ? c : throw new InvalidOperationException("our channel is not in memory");
        var fundingTxId = model.FundingOutput!.TransactionId!.Value.ToString();
        Channel? lndChannel = null;
        await ClusterPoll.UntilDoneAsync(async t =>
        {
            var ours = await nltg.FindChannelAsync(fundingTxId, t);
            var list = await lnd.Lightning.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: t);
            lndChannel = list.Channels.FirstOrDefault(x => x.ChannelPoint.StartsWith(fundingTxId, StringComparison.Ordinal));
            if (ours is { State: Domain.Channels.Enums.ChannelState.Open, ShortChannelId: not null }
             && lndChannel is { Active: true })
                return null;
            await topology.MineAndSyncAsync(1, t);
            return "the anchors channel is not usable on both ends yet";
        }, s_stepTimeout, "anchors channel usable on both ends", ct);

        // Assert: the channel type on both ends, VLS set up as AnchorsZeroFeeHtlc
        Assert.True(model.ChannelParams.OptionAnchorOutputs);
        Assert.Equal(CommitmentType.Anchors, lndChannel!.CommitmentType);

        // Act: approved liquidity to LND, then fractional payments both ways
        var (_, liquidity, _) = await PayApprovedAsync(signer, nltg, lnd, LiquidityMsat, ct);
        Assert.True(liquidity.Succeeded, liquidity.FailureReason);
        var (_, toUs, _) = await PayApprovedAsync(signer, lnd, nltg, ToUsMsat, ct);
        Assert.True(toUs.Succeeded, toUs.FailureReason);
        var (_, toPeer, _) = await PayApprovedAsync(signer, nltg, lnd, ToPeerMsat, ct);
        Assert.True(toPeer.Succeeded, toPeer.FailureReason);
        // Assert: exact msat on our side; LND's ListChannels reports whole satoshis (it is not the funder)
        const long ourMsat = CapacitySat * 1000 - LiquidityMsat + ToUsMsat - ToPeerMsat;
        await ClusterPoll.UntilDoneAsync(_ => Task.FromResult(
            model.Commitments is { } state && state.Htlcs.IsEmpty && state.LocalBalanceMsat == ourMsat
                ? null : $"our balance {model.Commitments?.LocalBalanceMsat} msat"), s_stepTimeout,
            "every HTLC settled at our exact msat balance", ct);
        await ClusterPoll.UntilDoneAsync(async t =>
        {
            var theirs = (await lnd.ListChannelsAsync(t)).FirstOrDefault(x => x.FundingTxId == fundingTxId);
            return theirs?.LocalBalanceMsat == (CapacitySat * 1000 - ourMsat) / 1000 * 1000
                ? null : $"LND's balance {theirs?.LocalBalanceMsat} msat";
        }, s_stepTimeout, "LND's balance at satoshi precision", ct);

        // Act: force close through the production command
        string commitmentTxId;
        using (var scope = nltg.TestNode.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<
                ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>>();
            var response = await handler.HandleAsync(new ForceCloseChannelClientRequest(channelId), ct);
            Assert.Equal("Broadcast", response.Status);
            Assert.NotNull(response.CommitmentTxId);
            commitmentTxId = new uint256((byte[])response.CommitmentTxId.Value).ToString();
        }
        await chain.Chain.WaitForMempoolAsync(commitmentTxId, ct, s_stepTimeout);
        var commitment = Transaction.Parse(
            (string)(await chain.Chain.Rpc.CallAsync("getrawtransaction",
                new Dictionary<string, object?> { ["txid"] = commitmentTxId }, ct))!, Network.RegTest);
        var anchorScript = new Script(Op.GetPushOp((byte[])model.LocalFundingPubKey), OpcodeType.OP_CHECKSIG,
            OpcodeType.OP_IFDUP, OpcodeType.OP_NOTIF, OpcodeType.OP_16, OpcodeType.OP_CHECKSEQUENCEVERIFY,
            OpcodeType.OP_ENDIF).WitHash.ScriptPubKey;
        var anchorIndex = commitment.Outputs.FindIndex(o => o.ScriptPubKey == anchorScript);
        Assert.True(anchorIndex >= 0, "our anchor is on the commitment");
        var commitmentFee = (long)CapacitySat - commitment.TotalOut.Satoshi;
        Log($"{run.Namespace}: commitment {commitmentTxId} fee {commitmentFee} sat, "
          + $"{(decimal)commitmentFee / commitment.GetVirtualSize():F2} sat/vB, our anchor {anchorIndex}");

        // The commitment alone is no longer minable (its fee is zero in Core's block template)
        await chain.Chain.Rpc.CallAsync("prioritisetransaction", new Dictionary<string, object?>
        { ["txid"] = commitmentTxId, ["fee_delta"] = -commitmentFee }, ct);

        // Assert: our child spends the anchor and a wallet input, signed by VLS
        Transaction? child = null;
        for (var attempt = 0; attempt < 6 && child is null; attempt++)
        {
            child = await FindAnchorChildAsync(chain, commitmentTxId, anchorIndex, TimeSpan.FromSeconds(20), ct);
            if (child is null)
                await topology.MineAndSyncAsync(1, ct);
        }
        Assert.NotNull(child);
        Assert.Contains(child.Inputs, i => i.PrevOut == new OutPoint(commitment.GetHash(), (uint)anchorIndex));
        Assert.True(child.Inputs.Count >= 2, "the child has a wallet fee input");
        var wallet = nltg.TestNode.Services.GetRequiredService<IUtxoMemoryRepository>();

        // Act: one block takes both, the commitment only through its child
        await topology.MineAndSyncAsync(1, ct);

        // Assert: Core confirmed the commitment and the child in the same block; the child's change is ours
        var commitmentInfo = await chain.Chain.WaitForConfirmationAsync(commitmentTxId, 1, ct, s_stepTimeout);
        var childInfo = await chain.Chain.WaitForConfirmationAsync(child.GetHash().ToString(), 1, ct, s_stepTimeout);
        Assert.Equal((string?)commitmentInfo.BlockHash, (string?)childInfo.BlockHash);
        await ClusterPoll.UntilDoneAsync(t => Task.FromResult(
            Enumerable.Range(0, child.Outputs.Count).Any(v => wallet.TryGetUtxo(child.GetHash().ToBytes(), (uint)v, out _))
                ? null : "the child's change is not in our wallet yet"), s_stepTimeout, "child change credited", ct);
        Log($"{run.Namespace}: VLS anchors channel to LND: child {child.GetHash()} ({child.Inputs.Count} inputs) "
          + $"confirmed the commitment at {watch.Elapsed.TotalSeconds:F1} s");
    }

    private static async Task<Transaction?> FindAnchorChildAsync(BitcoinCoreTopologyChain chain, string txId, int vout,
                                                                 TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var spending = await chain.Chain.Rpc.CallAsync("gettxspendingprevout", new Dictionary<string, object?>
            { ["outputs"] = new JArray(new JObject { ["txid"] = txId, ["vout"] = vout }) }, ct);
            var spender = (string?)spending.FirstOrDefault()?["spendingtxid"];
            if (spender is not null)
                return Transaction.Parse((string)(await chain.Chain.Rpc.CallAsync("getrawtransaction",
                    new Dictionary<string, object?> { ["txid"] = spender }, ct))!, Network.RegTest);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return null;
    }

    private static async Task<(TestInvoice Invoice, TestPaymentResult Result, int Attempts)> PayApprovedAsync(
        VlsGatewayFixture signer, ILightningTestPeer payer, ILightningTestPeer payee, long amountMsat,
        CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(amountMsat, "VLS anchors live acceptance", ct);
        if (payer is InProcessNode)
            new VlsPaymentApprovalClient(signer.ApprovalSocketPath, signer.ApprovalTokenFile)
               .AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11);
        var deadline = DateTime.UtcNow + s_stepTimeout;
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
}