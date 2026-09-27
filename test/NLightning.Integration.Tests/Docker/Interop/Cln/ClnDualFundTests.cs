using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Application.Channels.DualFunding;
using Domain.Bitcoin.Enums;
using Domain.Channels.DualFunding;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Fixtures;
using Utils;

/// <summary>
/// Proof DF of the splicing plan (<c>docs/agents/SPLICING_PLAN.md</c> "Optional wave DF", NL-037): BOLT 2 "Channel
/// Establishment v2" (<c>open_channel2</c>/<c>accept_channel2</c>, <c>option_dual_fund</c> 28/29) against Core
/// Lightning v26.06.8: CLN opens a dual-funded channel to us with our contribution, we open one to CLN and CLN matches
/// ours (<c>--funder-policy=match</c>), and we RBF our unconfirmed open. Each channel is used for payments both ways.
/// </summary>
/// <remarks>
/// <para>CLN v26.06.8 advertises <c>option_dual_fund</c> only with <c>--experimental-dual-fund</c> (checked with
/// <c>lightningd --help</c> on the pinned image), so these tests run a second CLN, <c>nltg-cln-df</c>, on the fixture's
/// bitcoind and network with that option and the funder plugin's <c>match</c> policy at 100 %. The fixture's own CLN is
/// left alone. Our node runs with <c>Features:AllowExperimentalFeatures</c> and <c>DualFund = Optional</c> (the feature
/// stays experimental until this proof is accepted, plan DF3) and registers the dual-funding services itself through
/// <see cref="NLightningTestNode.ConfigureServices"/> until the integrator adds <c>AddDualFundingServices()</c> to the
/// node composition.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnDualFundTests(ClnFixture fixture) : IAsyncLifetime
{
    private const string ContainerName = "nltg-cln-df";
    private const int P2PPort = 9735;

    // The fixture's bitcoind (ClnFixture keeps them private)
    private const int BitcoinRpcPort = 18443;
    private const string BitcoinRpcUser = "nltg";
    private const string BitcoinRpcPassword = "nltg";

    private static readonly TimeSpan s_usableTimeout = TimeSpan.FromMinutes(3);

    private readonly DockerClient _docker = new DockerClientConfiguration().CreateClient();
    private readonly List<NLightningTestNode> _nodes = [];
    private ClnClient _cln = null!;
    private string _clnNodeId = string.Empty;
    private int _clnHostPort;

    private string ClnAddress => $"{_clnNodeId}@127.0.0.1:{_clnHostPort}";
    private CompactPubKey ClnPubKey => Convert.FromHexString(_clnNodeId);

    public async ValueTask InitializeAsync()
    {
        await DockerContainerUtils.RemoveContainerAsync(_docker, ContainerName);
        _clnHostPort = await StartClnAsync();
        _cln = new ClnClient(_docker, ContainerName);
        await DockerContainerUtils.WaitUntilReadyAsync(ContainerName, async ct => await _cln.GetInfoAsync(ct),
                                                       TimeSpan.FromMinutes(2));
        _clnNodeId = (await _cln.GetInfoAsync(CancellationToken.None))["id"]!.GetValue<string>();

        // CLN's wallet: two confirmed outputs, so it can contribute and still fund its own open
        for (var i = 0; i < 2; i++)
            await FundClnAsync(LightningMoney.Satoshis(1_500_000), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();

        try
        {
            Console.WriteLine("[cln-df] UNUSUAL/BROKEN: "
                            + await _cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 40, "unusual"));
            if (DockerDiagnostics.CurrentTestFailed)
            {
                // The channel's daemons (dualopend, channeld; not onchaind) and the funder plugin's decisions
                var entries = (await _cln.CallAsync("getlog", CancellationToken.None, ("level", "debug")))["log"]!
                   .AsArray();
                var log = entries.Select(e => $"{e?["time"]} {e?["source"]}: {e?["log"]}")
                                 .Where(l => l.Contains("chan#", StringComparison.Ordinal)
                                          && !l.Contains("onchaind", StringComparison.Ordinal))
                                 .Take(250);
                Console.WriteLine("[cln-df] channel log:" + Environment.NewLine + string.Join(Environment.NewLine, log));
                Console.WriteLine("[cln-df] funder log: "
                                + await _cln.GetLogLinesAsync("funder", CancellationToken.None, 40, "debug"));
            }
        }
        catch
        {
            // Best effort
        }

        await DockerContainerUtils.RemoveContainerAsync(_docker, ContainerName);
        _docker.Dispose();
    }

    [Fact]
    public async Task Given_ClnOpensDualFunded_When_WeContribute_Then_TheChannelIsNormalAndCarriesPaymentsBothWays()
    {
        // Arrange: our node contributes 200,000 sat to a peer's v2 open
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-accepter", 200_000, ct);

        // Act: CLN opens 500,000 sat to us (fundchannel uses open_channel2 when both offer option_dual_fund)
        var opened = await _cln.CallAsync("fundchannel", ct, ("id", node.NodeIdHex), ("amount", "500000"),
                                          ("announce", "false"));
        var channelIdHex = opened["channel_id"]!.GetValue<string>();
        Console.WriteLine($"[cln-df] CLN fundchannel: {opened.ToJsonString()}");
        var channelId = new ChannelId(Convert.FromHexString(channelIdHex));
        await MineUntilUsableAsync(node, channelId, ct);

        // Assert: the v2 channel id from both revocation basepoints, both contributions in the channel
        var ours = Channel(node, channelId);
        Assert.Equal(ChannelVersion.V2, ours.Version);
        Assert.False(ours.IsInitiator);
        AssertV2ChannelId(ours);
        Assert.Equal(LightningMoney.Satoshis(200_000), ours.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(700_000), ours.FundingOutput!.Amount);
        var theirs = await _cln.GetPeerChannelAsync(node.NodeIdHex, channelIdHex, ct);
        Assert.Equal("CHANNELD_NORMAL", theirs!["state"]!.GetValue<string>());
        Assert.Equal(700_000_000L, theirs["total_msat"]!.GetValue<long>());
        Assert.Equal("local", theirs["opener"]!.GetValue<string>());

        // Act & Assert: payments both ways
        await PayBothWaysAsync(node, ct);
    }

    [Fact]
    public async Task Given_WeOpenDualFunded_When_ClnMatches_Then_TheChannelIsNormalAndCarriesPaymentsBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-opener", 0, ct);

        // Act: openchannel --dual-fund through the daemon's client handler, 400,000 sat of ours
        var channelId = await OpenThroughClientAsync(node, LightningMoney.Satoshis(400_000), ct);
        await MineUntilUsableAsync(node, channelId, ct);

        // Assert: CLN matched our 400,000 sat (funder-policy=match, 100 %)
        var ours = Channel(node, channelId);
        Assert.Equal(ChannelVersion.V2, ours.Version);
        Assert.True(ours.IsInitiator);
        AssertV2ChannelId(ours);
        Assert.Equal(LightningMoney.Satoshis(400_000), ours.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(800_000), ours.FundingOutput!.Amount);
        var theirs = await _cln.GetPeerChannelAsync(node.NodeIdHex, channelId.ToString(), ct);
        Assert.Equal("CHANNELD_NORMAL", theirs!["state"]!.GetValue<string>());
        Assert.Equal("remote", theirs["opener"]!.GetValue<string>());
        Assert.Equal(800_000_000L, theirs["total_msat"]!.GetValue<long>());

        await PayBothWaysAsync(node, ct);
    }

    [Fact]
    public async Task Given_OurUnconfirmedDualFundedOpen_When_WeBumpIt_Then_ClnFollowsTheReplacementAndItConfirms()
    {
        // Arrange: our open, not mined
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-rbf", 0, ct);
        var service = node.Services.GetRequiredService<IDualFundedOpenService>();
        // at 1,000 sat/kw: CLN's funder matches only inside its feerate bounds (253..2,530 sat/kw on the idle regtest),
        // and our RBF keeps both contributions, so the bump must stay inside them too
        var first = await service.OpenAsync(new DualFundedOpenRequest(ClnPubKey, LightningMoney.Satoshis(300_000),
                                                                      1_000), ct);
        Assert.True(first.FailureReason is null, first.FailureReason);
        await WaitInMempoolAsync(first.FundingTxId!.Value, ct);

        // Act: RBF at 2,000 sat/kw (IT-RBF-01 floor: 1,041)
        var bumped = await service.BumpAsync(first.ChannelId, 2_000, ct);

        // Assert: a new funding transaction that CLN follows, in the mempool instead of the first
        Assert.True(bumped.FailureReason is null, bumped.FailureReason);
        Assert.NotEqual(first.FundingTxId, bumped.FundingTxId);
        await WaitInMempoolAsync(bumped.FundingTxId!.Value, ct);
        var theirs = await _cln.GetPeerChannelAsync(node.NodeIdHex, first.ChannelId.ToString(), ct);
        Console.WriteLine($"[cln-df] CLN after the RBF: {theirs?.ToJsonString()}");
        Assert.Equal(TxIdDisplay(bumped.FundingTxId!.Value), theirs!["funding_txid"]!.GetValue<string>());

        // Act & Assert: it confirms and the channel works
        await MineUntilUsableAsync(node, first.ChannelId, ct);
        Assert.Equal(bumped.FundingTxId, Channel(node, first.ChannelId).FundingOutput!.TransactionId);
        Assert.Equal(LightningMoney.Satoshis(600_000), Channel(node, first.ChannelId).FundingOutput!.Amount);
        await PayBothWaysAsync(node, ct);
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, long acceptContributionSat,
                                                           CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(fixture.Bitcoin, name, configureNodeOptions: o =>
        {
            o.Features.AllowExperimentalFeatures = true;
            o.Features.DualFund = FeatureSupport.Optional;
        });
        _nodes.Add(node);
        node.ConfigureServices = services =>
        {
            services.AddDualFundingServices();
            services.Configure<DualFundingOptions>(o => o.AcceptContributionSat = acceptContributionSat);
        };

        await node.StartAsync(ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        await MineAndWaitAsync(node, 6, ct);
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(ClnAddress)).WaitAsync(ct);
        await Poll.UntilAsync(async () => node.IsConnectedTo(ClnPubKey)
                                       && await _cln.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), $"{name} and CLN connected", ct);
        return node;
    }

    private static async Task<ChannelId> OpenThroughClientAsync(NLightningTestNode node, LightningMoney amount,
                                                                CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            Daemon.Interfaces.IClientCommandHandler<OpenChannelClientRequest,
                Domain.Client.Responses.OpenChannelClientResponse>>();
        var response = await handler.HandleAsync(new OpenChannelClientRequest(string.Empty, amount)
        {
            NodeInfo = Convert.ToHexString((byte[])node.PeerManager.ListPeers().Single().NodeId),
            IsDualFunded = true
        }, ct);
        return response.ChannelId;
    }

    private static Domain.Channels.Models.ChannelModel Channel(NLightningTestNode node, ChannelId channelId) =>
        node.Services.GetRequiredService<Domain.Channels.Interfaces.IChannelMemoryRepository>()
            .TryGetChannel(channelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{node.Name} has no channel {channelId}");

    private static void AssertV2ChannelId(Domain.Channels.Models.ChannelModel channel)
    {
        using var sha256 = new Infrastructure.Crypto.Hashes.Sha256();
        Assert.Equal(ChannelIdV2.Derive(sha256, channel.LocalKeySet.RevocationCompactBasepoint,
                                        channel.RemoteKeySet!.RevocationCompactBasepoint), channel.ChannelId);
    }

    private async Task PayBothWaysAsync(NLightningTestNode node, CancellationToken ct)
    {
        // We pay CLN's invoice first (CLN may have contributed nothing to our open), then CLN pays ours
        var label = $"df-{Guid.NewGuid():N}";
        var theirInvoice = await _cln.CallAsync("invoice", ct, ("amount_msat", "30000000"), ("label", label),
                                                ("description", "df pays cln"));
        var payment = await node.PayInvoiceAsync(theirInvoice["bolt11"]!.GetValue<string>(), ct);
        Assert.Equal(Domain.Payments.Enums.PaymentStatus.Succeeded, payment.Status);

        var ourInvoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(15_000), "cln pays df", ct);
        var paid = await _cln.CallAsync("xpay", ct, ("invstring", ourInvoice.Bolt11!));
        Assert.Equal(15_000_000L, paid["amount_msat"]!.GetValue<long>());
    }

    private async Task MineUntilUsableAsync(NLightningTestNode node, ChannelId channelId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_usableTimeout;
        while (true)
        {
            var ours = node.Services.GetRequiredService<Domain.Channels.Interfaces.IChannelMemoryRepository>()
                           .TryGetChannel(channelId, out var channel)
                           ? channel.State
                           : ChannelState.None;
            var theirs = (await _cln.GetPeerChannelAsync(node.NodeIdHex, channelId.ToString(), ct))?["state"]
                           ?.GetValue<string>();
            var usable = ours == ChannelState.Open && theirs == "CHANNELD_NORMAL"
                      && (await node.GetChannelAsync(channelId, ct)).IsUsable();
            if (usable)
                return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Channel {channelId} not usable in time: ours {ours}, CLN {theirs}");

            await MineAndWaitAsync(node, 1, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task MineAndWaitAsync(NLightningTestNode node, int blocks, CancellationToken ct)
    {
        await fixture.MineAndWaitAsync(blocks, [node], ct);
        var tip = await fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);
        await Poll.UntilAsync(async () => (await _cln.GetInfoAsync(ct))["blockheight"]!.GetValue<long>() == tip,
                              TimeSpan.FromSeconds(60), "the dual-funding CLN at the tip", ct);
    }

    private async Task WaitInMempoolAsync(Domain.Bitcoin.ValueObjects.TxId txId, CancellationToken ct)
    {
        var display = NBitcoin.uint256.Parse(TxIdDisplay(txId));
        await Poll.UntilAsync(async () => (await fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(display),
                              TimeSpan.FromSeconds(60), $"{TxIdDisplay(txId)} in the mempool", ct);
    }

    /// <summary>The txid as bitcoind and CLN print it (reversed byte order).</summary>
    private static string TxIdDisplay(Domain.Bitcoin.ValueObjects.TxId txId) =>
        Convert.ToHexStringLower(((byte[])txId).Reverse().ToArray());

    private async Task FundClnAsync(LightningMoney amount, CancellationToken ct)
    {
        var address = (await _cln.CallAsync("newaddr", ct, ("addresstype", "bech32")))["bech32"]!.GetValue<string>();
        var txId = await fixture.Bitcoin.Rpc.SendToAddressAsync(
                       NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                       NBitcoin.Money.Satoshis(amount.Satoshi), cancellationToken: ct);
        await fixture.MineAsync(6, ct);
        await Poll.UntilAsync(async () =>
        {
            var outputs = (await _cln.CallAsync("listfunds", ct))["outputs"]!.AsArray();
            return outputs.Any(o => o?["txid"]?.GetValue<string>() == txId.ToString()
                                 && o["status"]?.GetValue<string>() == "confirmed");
        }, TimeSpan.FromSeconds(60), "the dual-funding CLN sees its deposit confirmed", ct);
    }

    private async Task<int> StartClnAsync()
    {
        var portKey = $"{P2PPort}/tcp";
        var container = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{ClnFixture.ClnImage}:{ClnFixture.ClnTag}",
            Name = ContainerName,
            Hostname = ContainerName,
            Env = ["LIGHTNINGD_NETWORK=regtest"],
            Cmd =
            [
                $"--bitcoin-rpcconnect={ClnFixture.BitcoinContainerName}", $"--bitcoin-rpcport={BitcoinRpcPort}",
                $"--bitcoin-rpcuser={BitcoinRpcUser}", $"--bitcoin-rpcpassword={BitcoinRpcPassword}",
                $"--bind-addr=0.0.0.0:{P2PPort}", "--alias=nltg-cln-df", "--log-level=debug", "--developer",
                "--dev-bitcoind-poll=1", "--experimental-dual-fund",
                // As opener CLN v26.06.8's lightningd tells dualopend the funding is locked at its own
                // funding-confirms (1 on regtest) while dualopend asserts the accepter's minimum_depth (ours: 3) is
                // reached, and dies (openingd/dualopend.c handle_funding_depth): keep the two equal
                "--funding-confirms=3", "--funder-lease-requests-only=false", "--funder-policy=match",
                "--funder-policy-mod=100", "--funder-min-their-funding=10000sat"
            ],
            ExposedPorts = new Dictionary<string, EmptyStruct> { [portKey] = default },
            HostConfig = new HostConfig
            {
                NetworkMode = ClnFixture.NetworkName,
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [portKey] = [new PortBinding { HostIP = "127.0.0.1", HostPort = string.Empty }]
                },
                ExtraHosts = OperatingSystem.IsLinux() ? [$"{ClnFixture.HostAddressFromContainers}:host-gateway"] : null
            }
        }) ?? throw new InvalidOperationException($"Failed to create {ContainerName}");
        await _docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters());

        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var inspect = await _docker.Containers.InspectContainerAsync(container.ID);
            if (inspect.NetworkSettings?.Ports is { } ports && ports.TryGetValue(portKey, out var bindings)
                                                            && bindings is { Count: > 0 }
                                                            && int.TryParse(bindings[0].HostPort, out var hostPort)
                                                            && hostPort > 0)
                return hostPort;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Docker did not publish the p2p port of {ContainerName}");
            await Task.Delay(100);
        }
    }
}