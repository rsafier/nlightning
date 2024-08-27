using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Lnrpc;
using NBitcoin;
using NLightning.Bolts.BOLT11.Types;
using NLightning.Common.Managers;
using NLightning.Common.Types;
using ServiceStack;
using ServiceStack.Text;
using Xunit.Abstractions;

namespace NLightning.Bolts.Tests.Docker;

using BOLT1.Fixtures;
using Bolts.BOLT1.Factories;
using Bolts.BOLT1.Primitives;
using Bolts.BOLT1.Services;
using Bolts.BOLT8.Dhs;
using Common.Constants;
using Fixtures;
using Utils;

#pragma warning disable xUnit1033 // Test classes decorated with 'Xunit.IClassFixture<TFixture>' or 'Xunit.ICollectionFixture<TFixture>' should add a constructor argument of type TFixture
[Collection("regtest")]
public class AbcNetworkTests
{
    private readonly LightningRegtestNetworkFixture _lightningRegtestNetworkFixture;

    public AbcNetworkTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _lightningRegtestNetworkFixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    [Fact]
    public async Task NLightning_BOLT8_Test_Connect_Alice()
    {
        // Arrange
        var localKeys = new SecP256K1().GenerateKeyPair();
        var hex = BitConverter.ToString(localKeys.PublicKey.ToBytes()).Replace("-", "");

        var alice = _lightningRegtestNetworkFixture.Builder?.LNDNodePool?.ReadyNodes.First(x => x.LocalAlias == "alice");
        Assert.NotNull(alice);

        var nodeOptions = new NodeOptions
        {
            ChainHashes = [ChainConstants.REGTEST],
            EnableDataLossProtect = true,
            EnableStaticRemoteKey = true,
            EnablePaymentSecret = true,
            KeyPair = localKeys
        };
        var peerService = new PeerService(nodeOptions, new TransportServiceFactory(), new PingPongServiceFactory(), new MessageServiceFactory());

        var aliceHost = new IPEndPoint((await Dns.GetHostAddressesAsync(alice.Host.SplitOnFirst("//")[1].SplitOnFirst(":")[0])).First(), 9735);

        // Act
        await peerService.ConnectToPeerAsync(new PeerAddress(new PubKey(alice.LocalNodePubKeyBytes), aliceHost.Address.ToString(), aliceHost.Port));
        var alicePeers = alice.LightningClient.ListPeers(new ListPeersRequest());

        // Assert
        Assert.NotNull(alicePeers.Peers.FirstOrDefault(x => x.PubKey.Equals(hex, StringComparison.CurrentCultureIgnoreCase)));
    }

    [Fact]
    public async Task NLightning_BOLT8_Test_Alice_Connect()
    {
        // Arrange
        var availablePort = await PortPool.GetAvailablePortAsync();
        var listener = new TcpListener(IPAddress.Any, availablePort);
        listener.Start();

        try
        {
            // Get ip from host
            var hostAddress = Environment.GetEnvironmentVariable("HOST_ADDRESS") ?? "host.docker.internal";

            var localKeys = new SecP256K1().GenerateKeyPair();
            var hex = BitConverter.ToString(localKeys.PublicKey.ToBytes()).Replace("-", "");

            var alice = _lightningRegtestNetworkFixture.Builder?.LNDNodePool?.ReadyNodes.First(x => x.LocalAlias == "alice");
            Assert.NotNull(alice);

            var nodeOptions = new NodeOptions
            {
                ChainHashes = [ChainConstants.REGTEST],
                EnableDataLossProtect = true,
                EnableStaticRemoteKey = true,
                EnablePaymentSecret = true,
                KeyPair = localKeys
            };
            var peerService = new PeerService(nodeOptions, new TransportServiceFactory(), new PingPongServiceFactory(), new MessageServiceFactory());

            _ = Task.Run(async () =>
            {
                {
                    var tcpClient = await listener.AcceptTcpClientAsync();

                    await peerService.AcceptPeerAsync(tcpClient);
                }
            });
            await Task.Delay(1000);

            // Act
            await alice.LightningClient.ConnectPeerAsync(new ConnectPeerRequest
            {
                Addr = new LightningAddress
                {
                    Host = $"{hostAddress}:{availablePort}",
                    Pubkey = hex
                }
            });
            var alicePeers = alice.LightningClient.ListPeers(new ListPeersRequest());

            // Assert
            Assert.NotNull(alicePeers.Peers.FirstOrDefault(x => x.PubKey.Equals(hex, StringComparison.CurrentCultureIgnoreCase)));
        }
        finally
        {
            listener.Dispose();
            PortPool.ReleasePort(availablePort);
        }
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
            var walletBalanceResponse = await node.LightningClient.WalletBalanceAsync(new WalletBalanceRequest());
            var channels = await node.LightningClient.ListChannelsAsync(new ListChannelsRequest());
            $"Node {node.LocalAlias} ({node.LocalNodePubKey})".Print();
            walletBalanceResponse.PrintDump();
            channels.PrintDump();
        }

        $"Bitcoin Node Balance: {(await _lightningRegtestNetworkFixture.Builder!.BitcoinRpcClient!.GetBalanceAsync()).Satoshi / 1e8}".Print();
    }

    [Fact]
    public async Task VirtalInvoiceInterceptionSample()
    {
        //Build the invoice
        var virtualNodeKey = new Key(); //Random node key
        SecureKeyManager.Initialize(virtualNodeKey.ToBytes());

        ConfigManager.Instance.Network = NLightning.Common.Types.Network.REG_TEST;
        var alice = await _lightningRegtestNetworkFixture.Builder!.WaitUntilAliasIsServerReady("alice");         //Node we will intercept at and use as hint
        var bob = await _lightningRegtestNetworkFixture.Builder!.WaitUntilAliasIsServerReady("bob");         //Node we will pay from

        var hashHex = RandomNumberGenerator.GetHexString(64);
        var paymentSecretHex = RandomNumberGenerator.GetHexString(64);
        var paymentHash = uint256.Parse(hashHex);
        var paymentSecret = uint256.Parse(paymentSecretHex); ;
        var invoice =
            new NLightning.Bolts.BOLT11.Invoice(10_000, "Hello NLightning, here is 10 sats", paymentHash, paymentSecret);

        var ri = new RoutingInfoCollection
        {
            new RoutingInfo(
            new PubKey(alice.LocalNodePubKeyBytes),
            new ShortChannelId(6102, 1, 1),
            1000,
            1000,
            144)
        };
        invoice.RoutingInfos = ri;
        var e = invoice.Encode();
        e.Print();
        //Setup interceptor to get virtual nodes stuff

        //Pay the thing

        //Did it work?

    }
}
#pragma warning restore xUnit1033 // Test classes decorated with 'Xunit.IClassFixture<TFixture>' or 'Xunit.ICollectionFixture<TFixture>' should add a constructor argument of type TFixture