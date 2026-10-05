using Grpc.Core;
using Grpc.Net.Client;
using NBitcoin;
using NBitcoin.RPC;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Tests.Utils;
using Npgsql;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Bark;
using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLightning.LnBackend;
using TestCollections;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.Bark;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Nodes.Postgres;
using Utils;
using BarkProto = BarkServer;
using CoreProto = Core;

/// <summary>
/// NL-1148 wave B proof of the Bark/ASP seam: an unmodified captaind (Second's Bark ASP server, the
/// <c>nltg-captaind</c> image built from the bark repo) uses our node's LN backend as its Lightning rail. The test
/// stands in for the receiving Bark wallet: it asks captaind for a hold invoice (captaind calls our <c>hold.Invoice</c>
/// through its <c>[[cln_array]]</c>), an LND peer pays it over a channel to our node, captaind sees the HTLC held
/// (<c>ACCEPTED</c>, through the <c>hold.TrackAll</c> stream we serve) and grants the Ark side, and once the preimage
/// reaches captaind's settlement WAL — the cross-process seam a claiming bark wallet drives through its
/// <c>ClaimLightningReceive</c> — captaind itself settles with our <c>hold.Settle</c> and LND's payment completes with
/// the preimage.
/// </summary>
/// <remarks>
/// <para>
/// captaind runs in the fixture's run namespace beside its own bitcoind (Core 31: captaind refuses older, and the
/// LND fixture's 29.0 is its own chain) and its own PostgreSQL. Its Lightning URIs are
/// <c>https://host.orb.internal:&lt;port&gt;</c>: captaind's tonic client configures TLS whatever the URI's scheme (an
/// <c>http://</c> URI dies parsing the certificate paths), so the backend serves <c>LnBackend:TlsDirectory</c> with the
/// test's CA — captaind's client certificate signed by the very <c>ca.pem</c> our side requires. The pods of
/// OrbStack's cluster reach that loopback listener at the same host the LND peers dial.
/// </para>
/// <para>
/// Deferred, honestly: the claim itself (<c>ClaimLightningReceive</c>'s arkoor package with musig nonces and taproot
/// trees) needs a full bark wallet, so the test writes the settlement row a claiming wallet's process would leave in
/// captaind's <c>htlc_settlements</c> WAL — the designed entry point for preimages learned outside captaind — and
/// everything from there is captaind's own code: the settler settles the hold invoice on our backend, marks the
/// subscription <c>SETTLED</c>, and LND completes.
/// </para>
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class BarkAspFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan s_bringUpTimeout = TimeSpan.FromMinutes(3);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    /// <summary>The receive's amount: above the pool's per-VTXO target so the grant has margin, well under alice's.</summary>
    private const ulong ReceiveSat = 60_000;

    /// <summary>The VTXO pool captaind keeps for lightning receive grants (the template's 1000/10000 sat targets are too
    /// small for one 60,000 sat grant).</summary>
    private const string VtxoPoolTargets = "vtxo_targets = [ \"60000sat:6\" ]";

    /// <summary>The gRPC protocol version header a current bark client sends (<c>server_rpc::pver</c>, hashlock clauses).</summary>
    private const string ProtocolVersion = "5";

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;
    private LnBackendHost? _lnBackend;
    private int _lnBackendPort;
    private BarkLnBackendTls? _tls;

    public BarkAspFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _lnBackendPort = await PortPoolUtil.GetAvailablePortAsync();
        _tls = BarkLnBackendTls.Create(_fixture.HostAddressForLnd);
        _node = await NLightningTestNode.CreateAsync(_fixture, "barkasp");
        // The mTLS listener captaind dials: its URIs are https, its client certificate signed by this ca.pem
        Node.ExtraConfiguration["LnBackend:Enabled"] = "true";
        Node.ExtraConfiguration["LnBackend:ListenAddress"] = "127.0.0.1";
        Node.ExtraConfiguration["LnBackend:Port"] = _lnBackendPort.ToString();
        Node.ExtraConfiguration["LnBackend:TlsDirectory"] = _tls.Directory;
        // The hosted server the daemon's own host starts (ConfigureNltgServices), which a test node's composition
        // does not include: register it through the node's service hook
        Node.ConfigureServices = services => services.AddLnBackendHost();
        await Node.StartAsync(TestContext.Current.CancellationToken);

        // The hosted service the daemon's host starts; a test node's service graph does not run hosted services
        _lnBackend = Node.Services.GetServices<IHostedService>().OfType<LnBackendHost>().Single();
        await _lnBackend.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(_lnBackendPort, _lnBackend.BoundPort);
        Console.WriteLine($"LN backend listening on 127.0.0.1:{_lnBackend.BoundPort} (mTLS) for captaind");
    }

    [Fact]
    public async Task Given_CaptaindOnOurLnBackend_When_LndPaysItsInvoice_Then_CaptaindHoldsAndSettlesIt()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange: captaind beside its own bitcoind and PostgreSQL, its Lightning rail our node's LN backend
        var run = _fixture.Cluster.Run;
        var postgres = await PostgresNode.DeployAsync(run, new PostgresNodeOptions { Name = "bark-postgres" },
                                                      s_bringUpTimeout, ct);
        var bitcoin = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Name = "bark-bitcoind",
            Image = ImageVersions.BitcoinCore31,
            Storage = NodeStorage.Ephemeral
        }, s_bringUpTimeout, ct);
        var lightningUri = $"https://{_fixture.HostAddressForLnd}:{_lnBackendPort}";
        var captaind = await CaptaindNode.DeployAsync(run, new CaptaindNodeOptions
        {
            LightningUri = lightningUri,
            HoldInvoiceUri = lightningUri,
            CaCertificatePem = _tls!.CaCertificatePem,
            ClientCertificatePem = _tls.ClientCertificatePem,
            ClientKeyPem = _tls.ClientKeyPem,
            BitcoindUrl = bitcoin.ClusterRpcUrl,
            BitcoindUser = bitcoin.Options.RpcUser,
            BitcoindPassword = bitcoin.Options.RpcPassword,
            PostgresHost = postgres.Host,
            PostgresPort = PostgresNode.Port,
            PostgresUser = postgres.Options.User,
            PostgresPassword = postgres.Options.Password,
            VtxoPoolTargets = VtxoPoolTargets
        }, s_bringUpTimeout, ct);
        Console.WriteLine($"captaind public gRPC at {captaind.PublicEndpoint} (LN backend {lightningUri})");

        using var channel = GrpcChannel.ForAddress($"http://{captaind.PublicEndpoint}");
        var ark = new BarkProto.ArkService.ArkServiceClient(channel);
        // The admin services (wallet status, the nursery report) bind their own socket, next to the public one
        using var adminChannel = GrpcChannel.ForAddress($"http://{captaind.AdminEndpoint}");
        var admin = new BarkProto.WalletAdminService.WalletAdminServiceClient(adminChannel);
        var nurseryAdmin = new BarkProto.NurseryAdminService.NurseryAdminServiceClient(adminChannel);
        var headers = new Metadata { { "pver", ProtocolVersion } };

        // The ASP answers once its Lightning rail is up: connecting to our backend is captaind's startup handshake
        // (our cln.Node Getinfo for the network check, then the hold client and its TrackAll monitor)
        var info = await Poll.ForAsync(async () =>
        {
            try
            {
                return await ark.GetArkInfoAsync(new CoreProto.Empty(), headers, deadline: DateTime.UtcNow.AddSeconds(5),
                                                 ct);
            }
            catch (RpcException)
            {
                return null;
            }
        }, s_timeout, "captaind's GetArkInfo answers", ct);
        Assert.Equal("regtest", info.Network);

        // One channel with alice, who pays the invoice from her pushed balance
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var channelOpened = await OpenUsableChannelAsync(alice, peerAddress, ct);

        // captaind's on-chain wallet funded and its VTXO pool stocked: what it grants lightning receives from
        var captainBitcoin = bitcoin.CreateNBitcoinClient(RpcRoute.PodIp, "miner");
        await MineMaturedCoinsAsync(captainBitcoin, 101, ct);
        var wallet = await Poll.ForAsync(async () => await admin.WalletStatusAsync(new CoreProto.Empty(), headers,
                                                                      deadline: DateTime.UtcNow.AddSeconds(5), ct),
                                         s_timeout, "captaind's wallet status", ct);
        var fundingAddress = BitcoinAddress.Create(wallet.Rounds.Address, Network.RegTest);
        await captainBitcoin.SendToAddressAsync(fundingAddress, Money.Coins(10m), ct);
        await MineMaturedCoinsAsync(captainBitcoin, 6, ct);
        Console.WriteLine($"captaind funded at {fundingAddress}, waiting for its VTXO pool");

        // The pool issues on chain-tip changes and its transactions run through the nursery: wait for one
        await Poll.ForAsync(async () =>
        {
            var nursery = await nurseryAdmin.ListNurseryTxsAsync(new BarkProto.ListNurseryTxsRequest
            {
                IncludeConfirmed = true
            }, headers, DateTime.UtcNow.AddSeconds(5), ct);
            return nursery.Txs.Any(t => t.Kind == "vtxopool") ? nursery : null;
        }, s_bringUpTimeout, "captaind's VTXO pool issued", ct);

        // Act: the receiving "wallet" (this test) asks captaind for an invoice — captaind calls our hold.Invoice
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var hash = new Hash(paymentHash);
        var receive = await ark.StartLightningReceiveAsync(new BarkProto.StartLightningReceiveRequest
        {
            PaymentHash = Google.Protobuf.ByteString.CopyFrom(paymentHash),
            AmountSat = ReceiveSat,
            MinCltvDelta = 80,
            Description = "nltg bark asp proof"
        }, headers, deadline: DateTime.UtcNow.AddSeconds(30), ct);
        Console.WriteLine($"captaind returned {receive.Bolt11[..40]}… through our LN backend");

        // Assert: the invoice is OURS — created by the backend's hold.Invoice call, not anywhere else
        var ours = await Poll.ForAsync(async () => await Node.GetInvoiceAsync(hash, ct), s_timeout,
                                       "our node holds the invoice captaind issued", ct);
        Assert.Equal(InvoiceStatus.Open, ours.Status);
        Assert.Equal(LightningMoney.Satoshis((long)ReceiveSat), ours.Amount);

        // alice pays: our node holds the HTLC without fulfilling it, and captaind sees ACCEPTED through our backend
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channelOpened.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        var paymentTask = await PayAndHearNothingAsync(alice, receive.Bolt11, lndChannel.ChanId, ct);
        var accepted = await Poll.ForAsync(async () =>
        {
            var check = await ark.CheckLightningReceiveAsync(new BarkProto.CheckLightningReceiveRequest
            {
                Hash = Google.Protobuf.ByteString.CopyFrom(paymentHash)
            }, headers, deadline: DateTime.UtcNow.AddSeconds(10), ct);
            return check.Status == BarkProto.LightningReceiveStatus.Accepted ? check : null;
        }, s_timeout, "captaind sees the invoice ACCEPTED (HTLC held)", ct);
        Console.WriteLine($"captaind's subscription is {accepted.Status}: the payment is held at our node");
        Assert.Equal(InvoiceStatus.Held, (await Node.GetInvoiceAsync(hash, ct))!.Status);

        // LND's payment sits in flight: nothing fulfilled it, the preimage is still only ours
        var inFlight = await GetLndPaymentAsync(alice, paymentHash, ct);
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, inFlight.Status);

        // The receiving wallet claims: captaind grants the Ark side (HTLC-recv VTXOs from its pool)…
        var fixtureTip = await _fixture.Bitcoin.GetBlockCountAsync(ct);
        var prepared = await Poll.ForAsync(async () =>
        {
            try
            {
                return await ark.PrepareLightningReceiveClaimAsync(new BarkProto.PrepareLightningReceiveClaimRequest
                {
                    PaymentHash = Google.Protobuf.ByteString.CopyFrom(paymentHash),
                    UserPubkey = Google.Protobuf.ByteString.CopyFrom(ClaimPublicKeyBytes()),
                    // The grant's expiry: within the inbound HTLC's (captaind checks the margin), above its own chain
                    HtlcRecvExpiry = (uint)fixtureTip - 2
                }, headers, deadline: DateTime.UtcNow.AddSeconds(30), ct);
            }
            catch (RpcException e)
            {
                Console.WriteLine($"prepare claim retried: {e.StatusCode} {e.Status.Detail}");
                return null;
            }
        }, s_timeout, "captaind prepared the lightning receive claim", ct);
        Assert.NotEmpty(prepared.HtlcVtxos);

        // …and reveals the preimage — the settlement WAL row its claiming process would leave in PostgreSQL
        await InsertSettlementAsync(postgres, paymentHash, preimage, ct);

        // Assert: captaind itself settles the hold invoice on our backend and LND's payment completes with the preimage
        var settled = await Poll.ForAsync(async () =>
        {
            var check = await ark.CheckLightningReceiveAsync(new BarkProto.CheckLightningReceiveRequest
            {
                Hash = Google.Protobuf.ByteString.CopyFrom(paymentHash)
            }, headers, deadline: DateTime.UtcNow.AddSeconds(10), ct);
            return check.Status == BarkProto.LightningReceiveStatus.Settled ? check : null;
        }, s_timeout, "captaind settled the lightning receive", ct);
        Console.WriteLine($"captaind's subscription is {settled.Status}");

        var payment = await paymentTask;
        Console.WriteLine($"LND's payment: {payment.Status} {payment.FailureReason}, {payment.Htlcs.Count} attempt(s)");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal((long)ReceiveSat * 1000, payment.ValueMsat);
        Assert.Equal(Convert.ToHexString(preimage), payment.PaymentPreimage, ignoreCase: true);

        // Our invoice is settled for the whole amount and the channel moved the money
        var settledInvoice = (await Node.GetInvoiceAsync(hash, ct))!;
        Assert.Equal(InvoiceStatus.Settled, settledInvoice.Status);
        Assert.Equal(LightningMoney.Satoshis((long)ReceiveSat), settledInvoice.AmountReceived);
        await Poll.UntilAsync(async () =>
        {
            var state = await Node.GetChannelAsync(channelOpened.ChannelId, ct);
            return state.OfferedHtlcCount + state.ReceivedHtlcCount == 0;
        }, s_timeout, "no HTLC pending on our channel", ct);
    }

    /// <summary>
    /// The <c>htlc_settlements</c> row a claiming process leaves: captaind's settler WAL is the designed cross-process
    /// seam for preimages (<c>server/src/ln/settler.rs</c>: settlements written by other processes, e.g. the watchman),
    /// its poller (configured to a second) picks the row up and the hold settler settles our invoice.
    /// </summary>
    private static async Task InsertSettlementAsync(PostgresNode postgres, byte[] paymentHash, byte[] preimage,
                                                    CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString("captaind"));
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO htlc_settlement (payment_hash, preimage, created_at)
                              VALUES (@hash, @preimage, NOW())
                              ON CONFLICT (payment_hash) DO NOTHING
                              """;
        command.Parameters.AddWithValue("hash", Convert.ToHexString(paymentHash).ToLowerInvariant());
        command.Parameters.AddWithValue("preimage", Convert.ToHexString(preimage).ToLowerInvariant());
        await command.ExecuteNonQueryAsync(ct);
        Console.WriteLine("settlement WAL row written: captaind's hold settler takes it from here");
    }

    /// <summary>The claiming wallet's public key: any secp256k1 point the server pins the grant's policy to.</summary>
    private static byte[] ClaimPublicKeyBytes()
    {
        using var key = new Key();
        return key.PubKey.ToBytes();
    }

    private async Task MineMaturedCoinsAsync(RPCClient bitcoin, int blocks, CancellationToken ct)
    {
        var address = await bitcoin.GetNewAddressAsync(ct);
        await bitcoin.GenerateToAddressAsync(blocks, address, ct);
    }

    private static async Task<Payment> GetLndPaymentAsync(LndNodeConnection alice, byte[] paymentHash,
                                                          CancellationToken ct)
    {
        var payments = await alice.LightningClient.ListPaymentsAsync(new ListPaymentsRequest
        {
            IncludeIncomplete = true
        },
                                                                    cancellationToken: ct);
        return Assert.Single(payments.Payments,
                             p => p.PaymentHash.Equals(Convert.ToHexString(paymentHash),
                                                       StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// LND starts paying <paramref name="bolt11"/> pinned to <paramref name="chanId"/> and the call is returned while
    /// the payment is still in flight (NL-319's outright failures are retried); the task later completes with the
    /// payment's final update.
    /// </summary>
    private static async Task<Task<Payment>> PayAndHearNothingAsync(LndNodeConnection alice, string bolt11,
                                                                    ulong chanId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var request = LndTestHelpers.PinnedPayment(bolt11, [chanId], timeoutSeconds: 170);
            var paymentTask = LndTestHelpers.SendPaymentV2Async(alice, request, ct, TimeSpan.FromMinutes(4));

            // Still no final update after the link's round trips: the HTLC is locked in at ours and held
            if (await Task.WhenAny(paymentTask, Task.Delay(TimeSpan.FromSeconds(10), deadline.Token)) != paymentTask)
                return paymentTask;

            var failed = await paymentTask;
            if (failed.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                          or PaymentFailureReason.FailureReasonNoRoute))
                throw new InvalidOperationException(
                    $"The payment towards the hold invoice ended with {failed.Status} {failed.FailureReason} before it "
                    + "could be held");

            Console.WriteLine($"{alice.LocalAlias}'s payment failed with {failed.FailureReason}; retrying");
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LndNodeConnection peer,
        string peerAddress, CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await Node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [Node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [Node], ct);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await _fixture.DumpLndLogsAsync(["alice"]);
        }

        if (_lnBackend is not null)
            await _lnBackend.StopAsync(CancellationToken.None);
        if (_node is not null)
            await _node.DisposeAsync();
        _tls?.Dispose();
        PortPoolUtil.ReleasePort(_lnBackendPort);
        GC.SuppressFinalize(this);
    }
}