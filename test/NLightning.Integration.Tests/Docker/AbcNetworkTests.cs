using System.Collections.Immutable;
using Lnrpc;
using NLightning.Tests.Utils;
using ServiceStack.Text;

namespace NLightning.Integration.Tests.Docker;

using Fixtures;
using Mock;
using TestCollections;
using Utils;

[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class AbcNetworkTests : IAsyncLifetime
{
    private readonly LightningRegtestNetworkFixture _lightningRegtestNetworkFixture;
    private readonly NLightningTestNode _node;

    public AbcNetworkTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _lightningRegtestNetworkFixture = fixture;
        Console.SetOut(new TestOutputWriter(output));

        var port = PortPoolUtil.GetAvailablePortAsync().GetAwaiter().GetResult();
        Assert.True(port > 0);
        _node = new NLightningTestNode(fixture, $"nlightning_{Guid.NewGuid()}.db", new FakeSecureKeyManager(), port);
    }

    public async ValueTask InitializeAsync()
    {
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NLightning_BOLT8_Test_Connect_Alice()
    {
        // Arrange
        var hex = Convert.ToHexString(_node.SecureKeyManager.GetNodePubKey());

        var alice =
            _lightningRegtestNetworkFixture.Builder?.LNDNodePool?.ReadyNodes.First(x => x.LocalAlias == "alice");
        Assert.NotNull(alice);

        // Act
        await _node.ConnectToAsync(alice, TestContext.Current.CancellationToken);
        var alicePeers =
            alice.LightningClient.ListPeers(new ListPeersRequest(),
                                            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(alicePeers.Peers.FirstOrDefault(x => x.PubKey
                                                             .Equals(hex, StringComparison.CurrentCultureIgnoreCase)));

        // Cleanup
        _node.PeerManager.DisconnectPeer(alice.LocalNodePubKeyBytes);
    }

    [Fact]
    public async Task NLightning_BOLT8_Test_Bob_Connect()
    {
        // Arrange
        var hostAddress = Environment.GetEnvironmentVariable("HOST_ADDRESS") ?? "host.docker.internal";
        var hex = Convert.ToHexString(_node.SecureKeyManager.GetNodePubKey());

        var bob = _lightningRegtestNetworkFixture.Builder?.LNDNodePool?.ReadyNodes
                                                 .First(x => x.LocalAlias == "bob");
        Assert.NotNull(bob);

        // Act
        await bob.LightningClient.ConnectPeerAsync(new ConnectPeerRequest
        {
            Addr = new LightningAddress
            {
                Host = $"{hostAddress}:{_node.Port}",
                Pubkey = hex
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        var bobPeers = bob.LightningClient.ListPeers(new ListPeersRequest(),
                                                     cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_node.PeerManager.GetPeer(bob.LocalNodePubKeyBytes) is null && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.NotNull(_node.PeerManager.GetPeer(bob.LocalNodePubKeyBytes));
        Assert.NotNull(
            bobPeers.Peers.FirstOrDefault(x => x.PubKey.Equals(hex, StringComparison.CurrentCultureIgnoreCase)));

        // Cleanup
        _node.PeerManager.DisconnectPeer(bob.LocalNodePubKeyBytes);
    }

    [Fact]
    public async Task Verify_Alice_Bob_Carol_Setup()
    {
        var readyNodes = _lightningRegtestNetworkFixture.Builder!.LNDNodePool!.ReadyNodes.ToImmutableList();
        var nodeCount = readyNodes.Count;
        Assert.Equal(3, nodeCount);
        $"LND Nodes in Ready State: {nodeCount}".Print();
        foreach (var node in readyNodes)
        {
            var walletBalanceResponse =
                await node.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(),
                                                              cancellationToken: TestContext.Current.CancellationToken);
            var channels =
                await node.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                             cancellationToken: TestContext.Current.CancellationToken);
            $"Node {node.LocalAlias} ({node.LocalNodePubKey})".Print();
            walletBalanceResponse.PrintDump();
            channels.PrintDump();
        }

        $"Bitcoin Node Balance: {(await _lightningRegtestNetworkFixture.Builder!.BitcoinRpcClient!.GetBalanceAsync()).Satoshi / 1e8}"
           .Print();
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        _node.DeleteFiles();
        PortPoolUtil.ReleasePort(_node.Port);
        GC.SuppressFinalize(this);
    }
}