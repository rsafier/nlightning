using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Invoicesrpc;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Routerrpc;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker;

using Domain.Bitcoin.Enums;
using Domain.Money;
using Fixtures;
using Google.Protobuf;
using LndGrpc;
using LndGrpc.Macaroons;
using LndGrpc.Tls;
using TestCollections;
using Utils;

/// <summary>
/// NL-1164 proof of the LND gRPC wave 2 surface against the fixture's real LND alice, driven only through our LND gRPC
/// with the in-tree LND client and its helpers (<see cref="LndTestHelpers"/>, unchanged): <c>ConnectPeer</c> to alice,
/// <c>OpenChannelSync</c> to her (answered at the published funding) until the channel is active, a
/// <c>routerrpc.SendPaymentV2</c> to an invoice of alice's reaching <c>SUCCEEDED</c> with <c>TrackPaymentV2</c>
/// agreeing, an <c>invoicesrpc.AddHoldInvoice</c> alice pays that <c>SubscribeSingleInvoice</c> sees
/// <c>ACCEPTED</c> then <c>SETTLED</c> after <c>SettleInvoice</c> (alice's payment succeeds with the preimage), a second
/// one canceled with <c>CancelInvoice</c> (alice's payment fails), and a cooperative <c>CloseChannel</c> streamed to its
/// confirmation, after which <c>ClosedChannels</c> lists it.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class LndGrpcWave2FlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "nltg-lndgrpc2-" + Guid.NewGuid().ToString("N")[..12]);

    private NLightningTestNode? _node;
    private LndGrpcHost? _host;
    private int _port;

    public LndGrpcWave2FlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _port = await PortPoolUtil.GetAvailablePortAsync();
        _node = await NLightningTestNode.CreateAsync(_fixture, "lndgrpc2");
        Node.ExtraConfiguration["LndGrpc:Enabled"] = "true";
        Node.ExtraConfiguration["LndGrpc:ListenAddress"] = "127.0.0.1";
        Node.ExtraConfiguration["LndGrpc:Port"] = _port.ToString();
        Node.ExtraConfiguration["LndGrpc:DataDirectory"] = _directory;
        Node.ConfigureServices = services => services.AddLndGrpcHost(_directory);
        await Node.StartAsync(TestContext.Current.CancellationToken);

        _host = Node.Services.GetServices<IHostedService>().OfType<LndGrpcHost>().Single();
        await _host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(_port, _host.BoundPort);
    }

    [Fact]
    public async Task Given_OurLndGrpc_When_ItOpensPaysHoldsAndClosesWithARealLnd_Then_EveryStepEndsAsLndWould()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        using var ours = await LndNodeConnection.ConnectAsync(
                             LndSettings.FromFiles($"https://127.0.0.1:{_port}",
                                                   Path.Combine(_directory, LndTlsFiles.CertificateFileName),
                                                   Path.Combine(_directory, LndMacaroonFiles.AdminFileName)),
                             cancellationToken: ct);

        // ConnectPeer and OpenChannelSync through our LND gRPC
        var aliceEndpoint = await _fixture.GetLndPeerEndpointAsync(alice, ct);
        var connected = await ours.LightningClient.ConnectPeerAsync(new ConnectPeerRequest
        {
            Addr = new LightningAddress { Pubkey = alice.LocalNodePubKey, Host = aliceEndpoint },
            Timeout = 60
        }, cancellationToken: ct);
        Console.WriteLine($"ConnectPeer: {connected.Status}");
        await Poll.UntilAsync(async () => (await ours.LightningClient.ListPeersAsync(new ListPeersRequest(),
                                                                                    cancellationToken: ct))
                                              .Peers.Any(p => p.PubKey == alice.LocalNodePubKey),
                              s_timeout, "alice listed in our ListPeers", ct);

        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var point = await ours.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom(alice.LocalNodePubKeyBytes),
            LocalFundingAmount = 1_000_000,
            PushSat = 300_000,
            Private = true,
            SatPerVbyte = 10
        }, cancellationToken: ct);
        var channelPoint = $"{Convert.ToHexStringLower(point.FundingTxidBytes.ToByteArray().Reverse().ToArray())}:"
                         + point.OutputIndex;
        Console.WriteLine($"OpenChannelSync answered {channelPoint}");
        var pending = await ours.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                       cancellationToken: ct);
        Assert.Contains(pending.PendingOpenChannels, c => c.Channel.ChannelPoint == channelPoint);

        var ourChannel = await Poll.ForAsync(async () =>
        {
            var mine = await LndTestHelpers.GetChannelByPointAsync(ours, channelPoint, ct);
            var hers = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
            if (mine is { Active: true } && hers is { Active: true })
                return mine;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], [Node], ct);
            return null;
        }, s_timeout, "the channel opened through our gRPC is active on both sides", ct);

        // routerrpc.SendPaymentV2 (the unchanged LND helper against our endpoint) to an invoice of alice's
        var aliceInvoice = await LndTestHelpers.AddInvoiceAsync(alice, 25_000_000, [], ct, memo: "from nltg");
        var paid = await Poll.ForAsync(async () =>
        {
            var payment = await LndTestHelpers.SendPaymentV2Async(
                              ours, LndTestHelpers.PinnedPayment(aliceInvoice.PaymentRequest, [ourChannel.ChanId]), ct);
            return payment.Status == Payment.Types.PaymentStatus.Succeeded ? payment : null;
        }, s_timeout, "our SendPaymentV2 to alice's invoice succeeded", ct);
        Assert.Equal(25_000_000, paid.ValueMsat);
        using (var track = ours.RouterClient.TrackPaymentV2(
                   new TrackPaymentRequest { PaymentHash = aliceInvoice.RHash }, cancellationToken: ct))
        {
            Assert.True(await track.ResponseStream.MoveNext(ct));
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, track.ResponseStream.Current.Status);
            Assert.Equal(paid.PaymentPreimage, track.ResponseStream.Current.PaymentPreimage);
        }

        Assert.Equal(Invoice.Types.InvoiceState.Settled,
                     (await LndTestHelpers.LookupInvoiceAsync(alice, aliceInvoice.RHash.ToByteArray(), ct)).State);

        // invoicesrpc: a hold invoice alice pays, ACCEPTED then SETTLED on our SubscribeSingleInvoice
        var (preimage, hash) = LndTestHelpers.NewPreimage();
        var hold = await ours.InvoiceClient.AddHoldInvoiceAsync(new AddHoldInvoiceRequest
        {
            Hash = ByteString.CopyFrom(hash),
            ValueMsat = 30_000_000,
            Memo = "hold via lnd grpc",
            Expiry = 600
        }, cancellationToken: ct);
        using var subscription = ours.InvoiceClient.SubscribeSingleInvoice(
            new SubscribeSingleInvoiceRequest { RHash = ByteString.CopyFrom(hash) }, cancellationToken: ct);
        Assert.True(await subscription.ResponseStream.MoveNext(ct));
        Assert.Equal(Invoice.Types.InvoiceState.Open, subscription.ResponseStream.Current.State);

        var holdPayment = LndTestHelpers.SendPaymentV2Async(
            alice, LndTestHelpers.PinnedPayment(hold.PaymentRequest, [ourChannel.ChanId], timeoutSeconds: 120), ct,
            TimeSpan.FromMinutes(3));
        Assert.True(await subscription.ResponseStream.MoveNext(ct));
        var accepted = subscription.ResponseStream.Current;
        Assert.Equal(Invoice.Types.InvoiceState.Accepted, accepted.State);
        Assert.Equal(30_000_000, accepted.AmtPaidMsat);
        Assert.Equal(ourChannel.ChanId, Assert.Single(accepted.Htlcs).ChanId);

        await ours.InvoiceClient.SettleInvoiceAsync(new SettleInvoiceMsg { Preimage = ByteString.CopyFrom(preimage) },
                                                    cancellationToken: ct);
        Assert.True(await subscription.ResponseStream.MoveNext(ct));
        Assert.Equal(Invoice.Types.InvoiceState.Settled, subscription.ResponseStream.Current.State);
        Assert.False(await subscription.ResponseStream.MoveNext(ct));
        var holdPaid = await holdPayment;
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, holdPaid.Status);
        Assert.Equal(Convert.ToHexStringLower(preimage), holdPaid.PaymentPreimage);

        // A second hold invoice, canceled: alice's payment fails back
        var (_, canceledHash) = LndTestHelpers.NewPreimage();
        var canceled = await ours.InvoiceClient.AddHoldInvoiceAsync(new AddHoldInvoiceRequest
        {
            Hash = ByteString.CopyFrom(canceledHash),
            ValueMsat = 5_000_000,
            Expiry = 600
        }, cancellationToken: ct);
        var canceledPayment = LndTestHelpers.SendPaymentV2Async(
            alice, LndTestHelpers.PinnedPayment(canceled.PaymentRequest, [ourChannel.ChanId], timeoutSeconds: 120), ct,
            TimeSpan.FromMinutes(3));
        await Poll.UntilAsync(async () =>
                                  (await ours.InvoiceClient.LookupInvoiceV2Async(
                                       new LookupInvoiceMsg { PaymentHash = ByteString.CopyFrom(canceledHash) },
                                       cancellationToken: ct)).State == Invoice.Types.InvoiceState.Accepted,
                              s_timeout, "the second hold invoice is ACCEPTED", ct);
        await ours.InvoiceClient.CancelInvoiceAsync(
            new CancelInvoiceMsg { PaymentHash = ByteString.CopyFrom(canceledHash) }, cancellationToken: ct);
        Assert.Equal(Payment.Types.PaymentStatus.Failed, (await canceledPayment).Status);
        Assert.Equal(Invoice.Types.InvoiceState.Canceled,
                     (await ours.LightningClient.LookupInvoiceAsync(
                          new PaymentHash { RHash = ByteString.CopyFrom(canceledHash) }, cancellationToken: ct)).State);

        // CloseChannel (cooperative) streamed to its confirmation
        await Poll.UntilAsync(async () => (await LndTestHelpers.GetChannelByPointAsync(ours, channelPoint, ct))
                                          ?.PendingHtlcs.Count == 0,
                              s_timeout, "no HTLC left on the channel", ct);
        using var close = ours.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint { FundingTxidBytes = point.FundingTxidBytes, OutputIndex = point.OutputIndex },
            SatPerVbyte = 5
        }, cancellationToken: ct);
        Assert.True(await close.ResponseStream.MoveNext(ct));
        var closingTxId = close.ResponseStream.Current.ClosePending.Txid;
        Assert.Equal(32, closingTxId.Length);
        var miner = Task.Run(async () =>
        {
            for (var i = 0; i < 12 && !ct.IsCancellationRequested; i++)
            {
                await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], [Node], ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }, ct);
        Assert.True(await close.ResponseStream.MoveNext(ct));
        var chanClose = close.ResponseStream.Current.ChanClose;
        Assert.True(chanClose.Success);
        Assert.Equal(closingTxId, chanClose.ClosingTxid);
        await miner;

        var closed = await ours.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(), cancellationToken: ct);
        var summary = Assert.Single(closed.Channels, c => c.ChannelPoint == channelPoint);
        Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, summary.CloseType);
        Assert.True(summary.SettledBalance > 0);
        Console.WriteLine($"closed {channelPoint}: settled {summary.SettledBalance} sat, close tx "
                        + summary.ClosingTxHash);
    }

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await _fixture.DumpLndLogsAsync(["alice"]);
        }

        if (_host is not null)
            await _host.StopAsync(CancellationToken.None);
        if (_node is not null)
            await _node.DisposeAsync();
        PortPoolUtil.ReleasePort(_port);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
        GC.SuppressFinalize(this);
    }
}