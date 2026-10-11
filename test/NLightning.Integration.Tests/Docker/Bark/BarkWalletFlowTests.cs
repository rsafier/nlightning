using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Bark;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLightning.LnBackend;
using TestCollections;
using Testing.Cluster.Nodes.Bark;
using Utils;

/// <summary>
/// NL-1148 wave C: a real Bark wallet — Second's <c>bark</c> CLI of the captaind commit — on an unmodified captaind
/// whose Lightning rail is our node's LN backend, end to end on both sides of the seam:
/// <list type="bullet">
///   <item>Pay: the wallet boards, then pays an LND invoice through captaind; captaind calls our <c>cln.Node</c>
///   <c>Xpay</c>, our node pays LND over a channel, the wallet's payment completes with LND's preimage and our
///   <c>ListPays</c> reports it <c>COMPLETE</c>.</item>
///   <item>Pay, still in flight past the retry window: LND holds the HTLC (a hold invoice) after captaind's
///   <c>retry_for</c>; our <c>Xpay</c> answers <c>DEADLINE_EXCEEDED</c>, captaind reconciles through <c>ListPays</c>
///   (<c>PENDING</c>) and keeps the wallet's HTLCs locked — never failing them while our HTLC is out — and once LND
///   settles, the wallet's payment completes with that preimage.</item>
///   <item>Pay a BOLT 12 offer of a second NLightning node: captaind fetches the invoice through our <c>FetchInvoice</c>
///   (an invoice_request over onion messages), the wallet pays it, captaind's xpay of the <c>lni</c> string pays it
///   over the paths our fetch verified, and our <c>ListPays</c> reports it <c>COMPLETE</c> with its BOLT 12 invoice.</item>
///   <item>Receive: the wallet's invoice (captaind's <c>hold.Invoice</c> on our node), LND pays it, and the wallet's own
///   claim (<c>PrepareLightningReceiveClaim</c> + <c>ClaimLightningReceive</c>, the arkoor package with its musig2
///   nonces) makes captaind settle our hold invoice — LND completes with the wallet's preimage.</item>
/// </list>
/// </summary>
/// <remarks>
/// captaind's chain is its own (Core 31), so the test keeps it at the LND fixture's height: captaind compares its own
/// tip with our <c>Getinfo</c> block height when it sizes the payment's <c>maxdelay</c>, requires the inbound HTLC's
/// expiry (our chain) to clear its own tip plus its delta (a receive's HTLC 10 blocks short was refused: "Incoming HTLC
/// expiry height doesn't fit") and the grant expiry (the wallet's tip plus its delta) to sit below it.
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class BarkWalletFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan s_bringUpTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_walletTimeout = TimeSpan.FromMinutes(3);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    private const string Prefix = "bw-";
    private const long BoardSat = 400_000;
    private const long PaySat = 50_000;
    private const long HeldPaySat = 30_000;
    private const long ReceiveSat = 60_000;
    private const long OfferSat = 20_000;

    /// <summary>The retry window the wallet asks captaind to give our xpay for the held payment (seconds).</summary>
    private const int HeldRetryForSeconds = 3;

    /// <summary>Lightning receive grants come from this pool (the template's targets are too small for 60,000 sat).</summary>
    private const string VtxoPoolTargets = "vtxo_targets = [ \"60000sat:6\" ]";

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;
    private LnBackendHost? _lnBackend;
    private int _lnBackendPort;
    private BarkLnBackendTls? _tls;
    private BarkAspStack? _stack;
    private BarkWalletNode? _wallet;
    private NLightningTestNode? _payee;

    public BarkWalletFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    private BarkAspStack Stack => _stack ?? throw new InvalidOperationException("captaind was not deployed");

    private BarkWalletNode Wallet => _wallet ?? throw new InvalidOperationException("The wallet was not deployed");

    public async ValueTask InitializeAsync()
    {
        _lnBackendPort = await PortPoolUtil.GetAvailablePortAsync();
        _tls = BarkLnBackendTls.Create(_fixture.HostAddressForLnd);
        _node = await NLightningTestNode.CreateAsync(_fixture, "barkwallet");
        Node.ExtraConfiguration["LnBackend:Enabled"] = "true";
        Node.ExtraConfiguration["LnBackend:ListenAddress"] = "127.0.0.1";
        Node.ExtraConfiguration["LnBackend:Port"] = _lnBackendPort.ToString();
        Node.ExtraConfiguration["LnBackend:TlsDirectory"] = _tls.Directory;
        Node.ConfigureServices = services => services.AddLnBackendHost();
        await Node.StartAsync(TestContext.Current.CancellationToken);

        _lnBackend = Node.Services.GetServices<IHostedService>().OfType<LnBackendHost>().Single();
        await _lnBackend.StartAsync(TestContext.Current.CancellationToken);
        Console.WriteLine($"LN backend listening on 127.0.0.1:{_lnBackend.BoundPort} (mTLS) for captaind");
    }

    [Fact]
    public async Task Given_ABarkWalletOnCaptaindOverOurLnBackend_When_ItPaysAndReceives_Then_EveryLegCompletesThroughUs()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange: captaind on our LN backend, a bark wallet beside it, and a channel with alice
        var lightningUri = $"https://{_fixture.HostAddressForLnd}:{_lnBackendPort}";
        _stack = await BarkAspStack.DeployAsync(_fixture, Prefix, lightningUri, _tls!, VtxoPoolTargets,
                                                s_bringUpTimeout, s_timeout, ct);
        _wallet = await BarkWalletNode.DeployAsync(_fixture.Cluster.Run, $"{Prefix}wallet", s_bringUpTimeout, ct);

        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var channelOpened = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channelOpened.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        // A second NLightning node with an offer: the BOLT 12 payee (onion messages and blinded paths through us)
        _payee = await NLightningTestNode.CreateAsync(_fixture, "barkpayee");
        await _payee.StartAsync(ct);
        var payeeChannel = await OpenPayeeChannelAsync(alice, _payee, ct);

        // captaind's chain at our height (mature coins included), its wallet funded and its VTXO pool stocked
        await AlignCaptaindChainAsync(101, ct);
        await Stack.FundAndStockPoolAsync(s_bringUpTimeout, ct);

        // The wallet: created against captaind and its chain, funded on chain and boarded into the Ark
        await Wallet.RunAsync(BarkWalletNode.CreateArguments(Stack.ArkUrl, Stack.Bitcoin.ClusterRpcUrl,
                                                             Stack.Bitcoin.Options.RpcUser,
                                                             Stack.Bitcoin.Options.RpcPassword), s_walletTimeout, ct);
        await BoardAsync(ct);

        await PayAsync(alice, ct);
        await PayHeldPastTheRetryWindowAsync(alice, ct);
        await PayOfferAsync(_payee, ct);
        await ReceiveAsync(alice, lndChannel.ChanId, ct);

        // Our channel carries nothing in flight after the three legs
        await Poll.UntilAsync(async () =>
        {
            var state = await Node.GetChannelAsync(channelOpened.ChannelId, ct);
            var payeeState = await Node.GetChannelAsync(payeeChannel, ct);
            return state.OfferedHtlcCount + state.ReceivedHtlcCount
                 + payeeState.OfferedHtlcCount + payeeState.ReceivedHtlcCount == 0;
        }, s_timeout, "no HTLC pending on our channels", ct);
    }

    /// <summary>
    /// Act/assert, pay a BOLT 12 offer: the wallet asks captaind for the offer's invoice (captaind calls our
    /// <c>cln.Node</c> <c>FetchInvoice</c>: an invoice_request over onion messages), then pays that invoice through
    /// captaind's xpay of the <c>lni</c> string; we pay it over the paths our fetch verified.
    /// </summary>
    private async Task PayOfferAsync(NLightningTestNode payee, CancellationToken ct)
    {
        var offers = payee.Services.GetRequiredService<IOfferService>();
        var offer = (await offers.CreateOfferAsync(new CreateOfferRequest(LightningMoney.Satoshis(OfferSat),
                                                                          "bark wallet pays an offer"), ct)).Offer;

        await Wallet.RunAsync(["lightning", "pay", "invoice", offer.Bolt12, "--wait"], s_walletTimeout, ct);

        // The payee was paid once for its offer, and our node paid it as a BOLT 12 payment of that offer
        Assert.Equal(1, (await offers.GetInvoiceCountsAsync(offer.OfferId, ct)).Paid);
        PaymentModel ours;
        using (var scope = Node.Services.CreateScope())
        {
            var payments = await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().ListAsync(0, 50);
            ours = Assert.Single(payments, p => p.Bolt12?.Offer == offer.Bolt12);
        }

        Assert.Equal(PaymentStatus.Succeeded, ours.Status);
        Assert.Equal("ln-backend", ours.Label);
        var status = await SendStatusAsync((byte[])ours.PaymentHash, ct);
        Assert.Equal("paid", (string?)status["state"]);
        Assert.Equal(Convert.ToHexString((byte[])ours.Preimage!), (string?)status["preimage"], ignoreCase: true);
        var pay = Assert.Single((await ListPaysAsync((byte[])ours.PaymentHash, ct)).Pays);
        Assert.Equal(Cln.ListpaysPays.Types.ListpaysPaysStatus.Complete, pay.Status);
        Assert.StartsWith("lni1", pay.Bolt12, StringComparison.Ordinal);
        Console.WriteLine($"bark wallet paid the payee's {OfferSat} sat offer through captaind's FetchInvoice + xpay");
    }

    /// <summary>Our channel to the BOLT 12 payee (it holds the anchors reserve as the fundee, NL-379).</summary>
    private async Task<ChannelId> OpenPayeeChannelAsync(LndNodeConnection alice, NLightningTestNode payee,
                                                        CancellationToken ct)
    {
        await payee.FundWalletAsync(LightningMoney.Satoshis(100_000), AddressType.P2Wpkh, ct);
        await Node.ConnectToAsync(payee, ct);
        var opened = await Node.OpenChannelAsync(
                         new OpenChannelClientRequest($"{payee.NodeIdHex}@127.0.0.1:{payee.Port}",
                                                      LightningMoney.Satoshis(300_000)), ct);
        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(opened.ChannelId, ct);
            var theirs = await payee.GetChannelAsync(opened.ChannelId, ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && theirs.IsUsable())
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], [Node, payee], ct);
            return false;
        }, s_timeout, $"channel {opened.ChannelId} to the payee usable on both sides", ct);
        Console.WriteLine($"Opened channel {opened.ChannelId} to the BOLT 12 payee");
        return opened.ChannelId;
    }

    /// <summary>Act/assert, pay: the wallet pays alice's invoice through captaind and our node.</summary>
    private async Task PayAsync(LndNodeConnection alice, CancellationToken ct)
    {
        var invoice = await LndTestHelpers.AddInvoiceAsync(alice, PaySat * 1_000, [], ct, memo: "bark wallet pays");
        var hash = invoice.RHash.ToByteArray();

        await Wallet.RunAsync(["lightning", "pay", "invoice", invoice.PaymentRequest, "--wait"], s_walletTimeout, ct);

        // The wallet holds LND's preimage
        var status = await SendStatusAsync(hash, ct);
        Assert.Equal("paid", (string?)status["state"]);
        var lndInvoice = await LndTestHelpers.LookupInvoiceAsync(alice, hash, ct);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, lndInvoice.State);
        Assert.Equal(Convert.ToHexString(lndInvoice.RPreimage.ToByteArray()), (string?)status["preimage"],
                     ignoreCase: true);
        Console.WriteLine($"bark wallet paid {PaySat} sat to alice through captaind and our node");

        // Our node paid it, and ListPays (captaind's reconciliation) reports it complete with the preimage
        var ours = await Node.GetPaymentAsync(new Hash(hash), ct);
        Assert.NotNull(ours);
        Assert.Equal(PaymentStatus.Succeeded, ours.Status);
        Assert.Equal("ln-backend", ours.Label);
        var pays = await ListPaysAsync(hash, ct);
        var pay = Assert.Single(pays.Pays);
        Assert.Equal(Cln.ListpaysPays.Types.ListpaysPaysStatus.Complete, pay.Status);
        Assert.Equal(lndInvoice.RPreimage.ToByteArray(), pay.Preimage.ToByteArray());
        Assert.Equal((ulong)PaySat * 1_000, pay.AmountMsat.Msat);
    }

    /// <summary>
    /// Act/assert, a payment still in flight after captaind's retry window: alice holds the HTLC. Our xpay answers
    /// DEADLINE_EXCEEDED after the window, captaind's ListPays reconciliation sees PENDING and keeps the attempt open
    /// (the wallet's send is neither paid nor revocable) through several reconciliation rounds; alice settles and the
    /// wallet's payment completes with her preimage.
    /// </summary>
    private async Task PayHeldPastTheRetryWindowAsync(LndNodeConnection alice, CancellationToken ct)
    {
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(alice, paymentHash, HeldPaySat * 1_000, [], ct,
                                                            memo: "bark wallet pays a held invoice");

        // Fire and forget, as a user's wallet does: the server pays in the background
        await Wallet.RunAsync(["lightning", "pay", "invoice", hold.PaymentRequest, "--retry-for",
                               HeldRetryForSeconds.ToString()], s_walletTimeout, ct);

        await LndTestHelpers.WaitForInvoiceStateAsync(alice, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        Console.WriteLine("alice holds the HTLC of the wallet's payment");

        // Past the retry window plus captaind's 15 s buffer, its reconciliation has run (backing off 1-5 s) several
        // times: the payment is still out at our node and pending everywhere
        await Task.Delay(TimeSpan.FromSeconds(HeldRetryForSeconds + 15 + 15), ct);
        var ours = await Node.GetPaymentAsync(new Hash(paymentHash), ct);
        Assert.NotNull(ours);
        Assert.Equal(PaymentStatus.InFlight, ours.Status);
        var pay = Assert.Single((await ListPaysAsync(paymentHash, ct)).Pays);
        Assert.Equal(Cln.ListpaysPays.Types.ListpaysPaysStatus.Pending, pay.Status);
        var pending = await SendStatusAsync(paymentHash, ct);
        Assert.Equal("payment-initiated", (string?)pending["state"]);
        Assert.Equal(Invoice.Types.InvoiceState.Accepted,
                     (await LndTestHelpers.LookupInvoiceAsync(alice, paymentHash, ct)).State);
        Console.WriteLine("past the retry window: our payment InFlight, ListPays PENDING, the wallet's send "
                        + "payment-initiated");

        // alice settles: our payment succeeds and captaind's next reconciliation completes the wallet's send
        await LndTestHelpers.SettleInvoiceAsync(alice, preimage, ct);
        var paid = await Poll.ForAsync(async () =>
        {
            var status = await SendStatusAsync(paymentHash, ct);
            return (string?)status["state"] == "paid" ? status : null;
        }, s_timeout, "the wallet's held payment completes", ct);
        Assert.Equal(Convert.ToHexString(preimage), (string?)paid["preimage"], ignoreCase: true);
        Assert.Equal(PaymentStatus.Succeeded, (await Node.GetPaymentAsync(new Hash(paymentHash), ct))!.Status);
        Console.WriteLine("alice settled: the wallet's payment completed with her preimage");
    }

    /// <summary>
    /// Act/assert, receive: the wallet's invoice is a hold invoice on our node; alice pays it, and the wallet's claim
    /// makes captaind settle it through our backend.
    /// </summary>
    private async Task ReceiveAsync(LndNodeConnection alice, ulong chanId, CancellationToken ct)
    {
        // captaind checks the inbound HTLC's expiry (our chain) against its own tip plus its delta, and the grant
        // expiry (the wallet's tip plus its delta) against the inbound HTLC's: one height for both chains
        await AlignCaptaindChainAsync(0, ct);

        var created = await Wallet.RunJsonAsync(["lightning", "invoice", $"{ReceiveSat} sat"], s_walletTimeout, ct);
        var bolt11 = (string)created["invoice"]!;
        var awaiting = await Wallet.RunJsonAsync(["lightning", "receive", "status", bolt11], s_walletTimeout, ct);
        Assert.Equal("awaiting-payment", (string?)awaiting["state"]);
        var paymentHash = Convert.FromHexString((string)awaiting["payment_hash"]!);
        var hash = new Hash(paymentHash);
        var ours = await Poll.ForAsync(async () => await Node.GetInvoiceAsync(hash, ct), s_timeout,
                                       "our node holds the wallet's invoice", ct);
        Assert.Equal(InvoiceStatus.Open, ours.Status);

        var paymentTask = await PayAndHearNothingAsync(alice, bolt11, chanId, ct);
        await Poll.UntilAsync(async () => (await Node.GetInvoiceAsync(hash, ct))?.Status == InvoiceStatus.Held,
                              s_timeout, "the wallet's invoice held at our node", ct);

        // The wallet claims: captaind grants the HTLC-recv VTXOs, the wallet reveals the preimage and captaind settles
        await Wallet.RunAsync(["lightning", "claim", bolt11, "--wait"], s_walletTimeout, ct);

        var payment = await paymentTask;
        Console.WriteLine($"LND's payment: {payment.Status} {payment.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(ReceiveSat * 1_000, payment.ValueMsat);
        var settled = (await Node.GetInvoiceAsync(hash, ct))!;
        Assert.Equal(InvoiceStatus.Settled, settled.Status);
        Assert.Equal(LightningMoney.Satoshis(ReceiveSat), settled.AmountReceived);
        var receive = await Wallet.RunJsonAsync(["lightning", "receive", "status", bolt11], s_walletTimeout, ct);
        Assert.Equal(payment.PaymentPreimage, (string?)receive["payment_preimage"], ignoreCase: true);
        Console.WriteLine($"the wallet claimed {ReceiveSat} sat: {(string?)receive["state"]}");
    }

    /// <summary>Funds the wallet on chain and boards; done once its spendable Ark balance shows the board.</summary>
    private async Task BoardAsync(CancellationToken ct)
    {
        var address = await Wallet.RunJsonAsync(["onchain", "address"], s_walletTimeout, ct);
        await Stack.Miner.SendToAddressAsync(BitcoinAddress.Create((string)address["address"]!, Network.RegTest),
                                             Money.Satoshis(BoardSat * 2), ct);
        await Stack.MineAsync(1, ct);
        await Wallet.RunAsync(["board", $"{BoardSat} sat"], s_walletTimeout, ct);

        // captaind accepts a board after required_board_confirmations (3); the wallet registers it on its next sync
        await Stack.MineAsync(3, ct);
        var balance = await Poll.ForAsync(async () =>
        {
            var current = await Wallet.RunJsonAsync(["balance"], s_walletTimeout, ct);
            if ((long)current["spendable_sat"]! > 0)
                return current;
            await Stack.MineAsync(1, ct);
            return null;
        }, s_bringUpTimeout, "the wallet's board is spendable", ct);
        Console.WriteLine($"the wallet boarded: {balance.ToJsonString()}");
    }

    private async Task<JsonNode> SendStatusAsync(byte[] paymentHash, CancellationToken ct) =>
        await Wallet.RunJsonAsync(["lightning", "pay", "status", Convert.ToHexString(paymentHash).ToLowerInvariant()],
                                  s_walletTimeout, ct);

    /// <summary>
    /// Brings both chains to one height (captaind's mined to at least <paramref name="minimumBlocks"/> more): the
    /// wallet's and captaind's heights stand in for ours (see the class remarks). The lower chain is mined; ours with
    /// alice and our node following it.
    /// </summary>
    private async Task AlignCaptaindChainAsync(int minimumBlocks, CancellationToken ct)
    {
        if (minimumBlocks > 0)
            await Stack.MineAsync(minimumBlocks, ct);
        var ours = await _fixture.Bitcoin.GetBlockCountAsync(ct);
        var theirs = await Stack.Miner.GetBlockCountAsync(ct);
        if (ours > theirs)
            await Stack.MineAsync(ours - theirs, ct);
        else if (theirs > ours)
            await ChainSync.MineAndWaitAsync(_fixture, theirs - ours, [_fixture.GetLndNode("alice")],
                                             _payee is null ? [Node] : [Node, _payee], ct);
        Console.WriteLine($"both chains at {Math.Max(ours, theirs)} (ours was {ours}, captaind's {theirs})");
    }

    /// <summary><c>ListPays</c> by payment hash over the backend's mTLS listener, as captaind calls it.</summary>
    private async Task<Cln.ListpaysResponse> ListPaysAsync(byte[] paymentHash, CancellationToken ct)
    {
        using var client = X509Certificate2.CreateFromPem(_tls!.ClientCertificatePem, _tls.ClientKeyPem);
        // An ephemeral PEM key cannot authenticate a TLS client on every platform: round-trip it through PKCS 12
        using var exportable = X509CertificateLoader.LoadPkcs12(client.Export(X509ContentType.Pkcs12), null);
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.ClientCertificates = [exportable];
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{_lnBackendPort}",
                                                   new GrpcChannelOptions { HttpHandler = handler });
        return await new Cln.Node.NodeClient(channel).ListPaysAsync(new Cln.ListpaysRequest
        {
            PaymentHash = Google.Protobuf.ByteString.CopyFrom(paymentHash)
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
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

            if (await Task.WhenAny(paymentTask, Task.Delay(TimeSpan.FromSeconds(10), deadline.Token)) != paymentTask)
                return paymentTask;

            var failed = await paymentTask;
            if (failed.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                          or PaymentFailureReason.FailureReasonNoRoute))
                throw new InvalidOperationException(
                    $"The payment towards the wallet's invoice ended with {failed.Status} {failed.FailureReason} "
                  + "before it could be held");

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
            if (_wallet is not null)
            {
                try
                {
                    Console.WriteLine("--- bark wallet debug.log (tail) ---");
                    Console.WriteLine(await _wallet.ReadDebugLogAsync(200, CancellationToken.None));
                    Console.WriteLine("--- captaind log (tail) ---");
                    Console.WriteLine(await Stack.Captaind.Handle.ReadLogAsync(300, CancellationToken.None));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"diagnostics failed: {e.Message}");
                }
            }

            await _fixture.DumpLndLogsAsync(["alice"]);
        }

        // The shared run namespace outlives this class: take this proof's pods out of it
        var run = _fixture.Cluster.Run;
        foreach (var name in (_wallet is null ? [] : new[] { _wallet.Handle.Name }).Concat(_stack?.NodeNames ?? []))
        {
            try
            {
                await run.RemoveNodeAsync(name, CancellationToken.None);
            }
            catch (Exception e)
            {
                Console.WriteLine($"removing {name} failed: {e.Message}");
            }
        }

        _stack?.Dispose();
        if (_lnBackend is not null)
            await _lnBackend.StopAsync(CancellationToken.None);
        if (_payee is not null)
            await _payee.DisposeAsync();
        if (_node is not null)
            await _node.DisposeAsync();
        _tls?.Dispose();
        PortPoolUtil.ReleasePort(_lnBackendPort);
        GC.SuppressFinalize(this);
    }
}