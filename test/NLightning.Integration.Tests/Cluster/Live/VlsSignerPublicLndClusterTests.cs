using System.Diagnostics;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Cluster.Live;

using Daemon.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Infrastructure.VlsSigning;
using LndGrpc.Mapping;
using RemoteSigning.Tests;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// Live proof of the public-channel, withdrawal and signmessage paths of the VLS signer (NL-1335) against LND: LND
/// learns our public channel and node by gossip (so it verified our VLS-signed announcements), verifies a message we
/// signed through VLS, and Bitcoin Core accepts and confirms a withdrawal VLS signed to an operator-allowlisted address.
/// The withdrawal's wallet signing is the anchors lane's (<c>VlsLightningSigner.Anchors.cs</c>, NL-1325).
/// </summary>
[Trait("Category", "Cluster")]
public class VlsSignerPublicLndClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 1_000_000;
    private const long WithdrawSat = 250_000;
    private const string Alias = "nltg-vls-public";

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_AVlsNode_When_ItOpensAPublicChannelSignsAMessageAndWithdraws_Then_LndAndCoreAcceptEverySignature()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await using var signer = new VlsGatewayFixture();
        await signer.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = signer.SocketPath, TokenFile = signer.TokenFile, Network = "regtest" });
        var keys = new VlsSecureKeyManager(connection);
        await using var run = await TestRun.StartAsync(
            TestRunOptions.FromEnvironment("vls-public-lnd") with { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        await using var deployer = new InProcessNodeDeployer
        {
            KeyManager = _ => keys,
            PodFacingHost = Environment.GetEnvironmentVariable("NLTG_ADOPT_NAMESPACE") == "1"
                ? System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                        .First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString()
                : null,
            ConfigureNodeOptions = (_, options) =>
            {
                // The test node's defaults enable features outside the VLS profile, which the start validates
                var features = options.Features;
                features.OptionAnchors = features.DualFund = features.OptionQuiesce = features.OptionSplice =
                    features.OptionSimpleTaproot = features.OptionGossipV2 = features.OptionSimpleClose =
                    features.OptionRouteBlinding = features.OptionOnionMessages = features.OptionTrampolineRouting =
                    features.OptionProvideStorage = features.BeyondSegwitShutdown = features.ZeroConf =
                    Domain.Enums.FeatureSupport.No;
                options.MaxDustHtlcExposureMsat = 0;
                options.HtlcMinimumAmount = Domain.Money.LightningMoney.Satoshis(1_000);
            },
            ConfigureNode = node =>
            {
                node.ConfigureServices = services => services.AddLogging(builder =>
                    builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
                node.VlsSignerConnection = connection;
                node.ExtraConfiguration["Signing:Mode"] = "Vls";
                node.ExtraConfiguration["Signing:SocketPath"] = signer.SocketPath;
                node.ExtraConfiguration["Signing:AuthTokenFile"] = signer.TokenFile;
                node.ExtraConfiguration["Node:Alias"] = Alias;
                // Announce our own gossip quickly (the regtest default flushes less often)
                node.ExtraConfiguration["Gossip:OwnGossipFlushInterval"] = "00:00:05";
            }
        };
        using var topology = await new TopologyBuilder
        { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_stepTimeout }
            .AddBitcoinCore("miner").AddNLightning("nltg").AddLnd("lnd")
            .UseInProcessNodes(deployer).FundWallet("nltg", WalletSat)
            .AddChannel("nltg", "lnd", CapacitySat, announce: true).BuildAsync(run, ct);
        var nltg = topology.InProcessNode("nltg");
        var lnd = topology.Node<LndNode>("lnd");
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        var ourId = await nltg.GetNodeIdAsync(ct);
        var channel = Assert.Single(topology.Channels);
        Assert.IsType<VlsLightningSigner>(nltg.TestNode.Services.GetRequiredService<ILightningSigner>());

        // Act: blocks until LND has our channel with both policies and our node_announcement, all signed by VLS
        var lndChannels = await lnd.Lightning.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: ct);
        var chanId = Assert.Single(lndChannels.Channels,
                                   c => c.ChannelPoint.StartsWith(channel.Open.FundingTxId, StringComparison.Ordinal))
                           .ChanId;
        var edge = await ClusterPoll.ForAsync(async c =>
        {
            await topology.MineAndSyncAsync(1, c);
            var found = await TryGetChanInfoAsync(lnd, chanId, c);
            return found is { Node1Policy: not null, Node2Policy: not null } ? found : null;
        }, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(2), "LND has our public channel with both policies", ct);
        var node = await ClusterPoll.ForAsync(async c =>
        {
            var info = await TryGetNodeInfoAsync(lnd, ourId, c);
            return info?.Node is { Alias: Alias } ? info : null;
        }, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1), "LND has our node_announcement", ct);

        // Assert: LND accepted the VLS node and funding signatures (it drops an announcement that does not verify)
        Assert.Contains(ourId, new[] { edge.Node1Pub, edge.Node2Pub });
        Assert.Equal(CapacitySat, edge.Capacity);
        var ourPolicy = edge.Node1Pub == ourId ? edge.Node1Policy : edge.Node2Policy;
        Assert.False(ourPolicy.Disabled);
        Assert.Equal(Alias, node.Node.Alias);
        Log($"{run.Namespace}: LND learned public channel {chanId} and node {ourId[..16]}… by gossip at "
          + $"{watch.Elapsed.TotalSeconds:F1} s");

        // Act: signmessage through VLS, verified by LND
        var message = "NLightning VLS signmessage live proof"u8.ToArray();
        var signature = nltg.TestNode.Services.GetRequiredService<ILightningSigner>()
                            .SignLightningMessage(message, singleHash: false);
        var verified = await lnd.Lightning.VerifyMessageAsync(new VerifyMessageRequest
        {
            Msg = ByteString.CopyFrom(message),
            Signature = ZBase32.Encode(signature)
        }, cancellationToken: ct);

        // Assert
        Assert.True(verified.Valid);
        Assert.Equal(ourId, verified.Pubkey);

        // Act: a withdrawal to an address VLS has not allowlisted is refused and publishes nothing
        var destination = new Key().PubKey.WitHash.GetAddress(Network.RegTest).ToString();
        await Assert.ThrowsAnyAsync<Exception>(() => WithdrawAsync(nltg, destination, ct));

        // Act: the operator allowlists it through the approval socket; the node then withdraws
        new VlsWalletApprovalClient(signer.ApprovalSocketPath, signer.ApprovalTokenFile)
           .AllowlistAddress(Guid.NewGuid(), destination);
        var withdrawal = await WithdrawAsync(nltg, destination, ct);
        var txId = new uint256((byte[])withdrawal.TxId).ToString();
        await chain.Chain.WaitForMempoolAsync(txId, ct, s_stepTimeout);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        var confirmed = await chain.Chain.WaitForConfirmationAsync(txId, TopologyDeployer.ConfirmationBlocks, ct,
                                                                   s_stepTimeout);

        // Assert: Core confirmed it, paying the destination and the change back to the VLS wallet
        Assert.True(withdrawal.Published);
        Assert.True(confirmed.Confirmations >= TopologyDeployer.ConfirmationBlocks);
        Assert.Equal(WithdrawSat, withdrawal.AmountSat);
        var transaction = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = txId, ["verbose"] = true }, ct);
        var outputs = transaction["vout"]!.Children().ToList();
        Assert.Contains(outputs, o => (string?)o!["scriptPubKey"]!["address"] == destination
                                    && Money.Coins((decimal)o["value"]!).Satoshi == WithdrawSat);
        if (withdrawal.ChangeSat > 0)
        {
            var change = Assert.Single(outputs, o => (string?)o!["scriptPubKey"]!["address"] != destination);
            Assert.Equal(withdrawal.ChangeSat, Money.Coins((decimal)change!["value"]!).Satoshi);
            var changeScript = BitcoinAddress.Create((string)change["scriptPubKey"]!["address"]!, Network.RegTest)
                                             .ScriptPubKey;
            Assert.Contains(Enumerable.Range(0, 64), i =>
                new PubKey((byte[])keys.GetWalletPublicKey((uint)i, true, Domain.Bitcoin.Enums.AddressType.P2Wpkh))
                   .WitHash.ScriptPubKey == changeScript);
        }

        Log($"{run.Namespace}: VLS withdrawal {txId} ({WithdrawSat} sat, fee {withdrawal.FeeSat} sat, change "
          + $"{withdrawal.ChangeSat} sat) confirmed at {watch.Elapsed.TotalSeconds:F1} s");
    }

    private static async Task<WithdrawClientResponse> WithdrawAsync(InProcessNode node, string address,
                                                                    CancellationToken ct)
    {
        using var scope = node.TestNode.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<WithdrawClientRequest, WithdrawClientResponse>>();
        return await handler.HandleAsync(new WithdrawClientRequest(address, WithdrawSat, 2), ct);
    }

    private static async Task<ChannelEdge?> TryGetChanInfoAsync(LndNode lnd, ulong chanId, CancellationToken ct)
    {
        try
        {
            return await lnd.Lightning.GetChanInfoAsync(new ChanInfoRequest { ChanId = chanId }, cancellationToken: ct);
        }
        catch (RpcException)
        {
            return null;
        }
    }

    private static async Task<NodeInfo?> TryGetNodeInfoAsync(LndNode lnd, string nodeId, CancellationToken ct)
    {
        try
        {
            return await lnd.Lightning.GetNodeInfoAsync(new NodeInfoRequest { PubKey = nodeId }, cancellationToken: ct);
        }
        catch (RpcException)
        {
            return null;
        }
    }
}