using Grpc.Core;
using Grpc.Net.Client;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Docker.Bark;

using Fixtures;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.Bark;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Nodes.Postgres;
using Utils;
using BarkProto = BarkServer;
using CoreProto = Core;

/// <summary>
/// captaind as the Bark proofs run it (NL-1148): beside its own bitcoind (Core 31: captaind refuses older, so it is a
/// chain of its own next to the LND fixture's) and its own PostgreSQL in the fixture's run namespace, its Lightning
/// rail an NLightning LN backend over mutual TLS, and its public and admin gRPC clients. The pod names carry a prefix
/// so two proofs of one collection never collide in the shared namespace.
/// </summary>
public sealed class BarkAspStack : IDisposable
{
    /// <summary>The gRPC protocol version header a current bark client sends (<c>server_rpc::pver</c>).</summary>
    public const string ProtocolVersion = "5";

    private readonly GrpcChannel _channel;
    private readonly GrpcChannel _adminChannel;

    private BarkAspStack(string prefix, PostgresNode postgres, BitcoinCoreNode bitcoin, CaptaindNode captaind)
    {
        Prefix = prefix;
        Postgres = postgres;
        Bitcoin = bitcoin;
        Captaind = captaind;
        _channel = GrpcChannel.ForAddress($"http://{captaind.PublicEndpoint}");
        _adminChannel = GrpcChannel.ForAddress($"http://{captaind.AdminEndpoint}");
        Ark = new BarkProto.ArkService.ArkServiceClient(_channel);
        // The admin services (wallet status, the nursery report) bind their own socket, next to the public one
        Admin = new BarkProto.WalletAdminService.WalletAdminServiceClient(_adminChannel);
        NurseryAdmin = new BarkProto.NurseryAdminService.NurseryAdminServiceClient(_adminChannel);
        Miner = bitcoin.CreateNBitcoinClient(RpcRoute.PodIp, "miner");
    }

    /// <summary>The pod-name prefix of this stack's nodes.</summary>
    public string Prefix { get; }

    public PostgresNode Postgres { get; }

    public BitcoinCoreNode Bitcoin { get; }

    public CaptaindNode Captaind { get; }

    /// <summary>captaind's chain's mining wallet.</summary>
    public RPCClient Miner { get; }

    public BarkProto.ArkService.ArkServiceClient Ark { get; }

    public BarkProto.WalletAdminService.WalletAdminServiceClient Admin { get; }

    public BarkProto.NurseryAdminService.NurseryAdminServiceClient NurseryAdmin { get; }

    public Metadata Headers { get; } = new() { { "pver", ProtocolVersion } };

    /// <summary>The Ark server URL a bark wallet in the run namespace dials (<c>--ark</c>).</summary>
    public string ArkUrl => $"http://{Captaind.PublicEndpoint}";

    /// <summary>
    /// Deploys PostgreSQL, bitcoind and captaind (names <paramref name="prefix"/>postgres, …bitcoind, …captaind) with
    /// captaind's Lightning rail at <paramref name="lightningUri"/> and waits until captaind's <c>GetArkInfo</c>
    /// answers — connecting to our backend is its startup handshake (<c>cln.Node</c> Getinfo for the network check,
    /// then the hold client and its TrackAll monitor).
    /// </summary>
    public static async Task<BarkAspStack> DeployAsync(LightningRegtestNetworkFixture fixture, string prefix,
                                                       string lightningUri, BarkLnBackendTls tls,
                                                       string vtxoPoolTargets, TimeSpan bringUpTimeout,
                                                       TimeSpan timeout, CancellationToken ct)
    {
        var run = fixture.Cluster.Run;
        var postgres = await PostgresNode.DeployAsync(run, new PostgresNodeOptions { Name = $"{prefix}postgres" },
                                                      bringUpTimeout, ct);
        var bitcoin = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Name = $"{prefix}bitcoind",
            Image = ImageVersions.BitcoinCore31,
            Storage = NodeStorage.Ephemeral
        }, bringUpTimeout, ct);
        var captaind = await CaptaindNode.DeployAsync(run, new CaptaindNodeOptions
        {
            Name = $"{prefix}captaind",
            LightningUri = lightningUri,
            HoldInvoiceUri = lightningUri,
            CaCertificatePem = tls.CaCertificatePem,
            ClientCertificatePem = tls.ClientCertificatePem,
            ClientKeyPem = tls.ClientKeyPem,
            BitcoindUrl = bitcoin.ClusterRpcUrl,
            BitcoindUser = bitcoin.Options.RpcUser,
            BitcoindPassword = bitcoin.Options.RpcPassword,
            PostgresHost = postgres.Host,
            PostgresPort = PostgresNode.Port,
            PostgresUser = postgres.Options.User,
            PostgresPassword = postgres.Options.Password,
            VtxoPoolTargets = vtxoPoolTargets
        }, bringUpTimeout, ct);
        Console.WriteLine($"captaind public gRPC at {captaind.PublicEndpoint} (LN backend {lightningUri})");

        var stack = new BarkAspStack(prefix, postgres, bitcoin, captaind);
        var info = await Poll.ForAsync(async () =>
        {
            try
            {
                return await stack.Ark.GetArkInfoAsync(new CoreProto.Empty(), stack.Headers,
                                                       deadline: DateTime.UtcNow.AddSeconds(5), ct);
            }
            catch (RpcException)
            {
                return null;
            }
        }, timeout, "captaind's GetArkInfo answers", ct);
        if (info.Network != "regtest")
            throw new InvalidOperationException($"captaind runs on {info.Network}, not regtest");
        return stack;
    }

    /// <summary>Mines <paramref name="blocks"/> blocks on captaind's chain.</summary>
    public async Task MineAsync(int blocks, CancellationToken ct)
    {
        var address = await Miner.GetNewAddressAsync(ct);
        await Miner.GenerateToAddressAsync(blocks, address, ct);
    }

    /// <summary>
    /// Funds captaind's on-chain wallet and waits until its VTXO pool issued (what lightning receive grants are paid
    /// from): the pool issues on chain-tip changes, its transactions run through the nursery.
    /// </summary>
    public async Task FundAndStockPoolAsync(TimeSpan timeout, CancellationToken ct)
    {
        var wallet = await Poll.ForAsync(async () => await Admin.WalletStatusAsync(new CoreProto.Empty(), Headers,
                                                         deadline: DateTime.UtcNow.AddSeconds(5), ct),
                                         timeout, "captaind's wallet status", ct);
        var fundingAddress = BitcoinAddress.Create(wallet.Rounds.Address, Network.RegTest);
        await Miner.SendToAddressAsync(fundingAddress, Money.Coins(10m), ct);
        await MineAsync(6, ct);
        Console.WriteLine($"captaind funded at {fundingAddress}, waiting for its VTXO pool");

        await Poll.ForAsync(async () =>
        {
            var nursery = await NurseryAdmin.ListNurseryTxsAsync(new BarkProto.ListNurseryTxsRequest
            {
                IncludeConfirmed = true
            }, Headers, DateTime.UtcNow.AddSeconds(5), ct);
            return nursery.Txs.Any(t => t.Kind == "vtxopool") ? nursery : null;
        }, timeout, "captaind's VTXO pool issued", ct);
    }

    /// <summary>The stack's node names, for removal from the run.</summary>
    public IReadOnlyList<string> NodeNames => [Captaind.Handle.Name, Bitcoin.Handle.Name, Postgres.Handle.Name];

    public void Dispose()
    {
        _channel.Dispose();
        _adminChannel.Dispose();
    }
}