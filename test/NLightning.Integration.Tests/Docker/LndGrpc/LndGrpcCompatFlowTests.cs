using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using LndGrpc;
using LndGrpc.Macaroons;
using LndGrpc.Tls;
using TestCollections;
using Utils;

/// <summary>
/// NL-1161 proof of LND gRPC compatibility: our node serves LND's <c>lnrpc.Lightning</c> (<c>LndGrpc:Enabled</c>) and
/// the test drives it only through <see cref="LndNodeConnection"/> — the in-tree LND client this suite drives the
/// real LND 0.21.4 nodes with — pinned to the <c>tls.cert</c> and carrying the <c>admin.macaroon</c> our node made.
/// Against the fixture's real LND alice: the channel our <c>ListChannels</c> reports is the one alice reports (chan_id,
/// capacity, both balances and the commitment fee, LND's net-of-fees convention on both sides), an invoice made with
/// our <c>AddInvoice</c> is paid by alice and our <c>LookupInvoice</c>/<c>ListInvoices</c>/<c>ChannelBalance</c>
/// show it settled, and alice's <c>VerifyMessage</c> recovers our key from our <c>SignMessage</c>.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class LndGrpcCompatFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    private const long InvoiceMsat = 42_000_000;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "nltg-lndgrpc-" + Guid.NewGuid().ToString("N")[..12]);

    private NLightningTestNode? _node;
    private LndGrpcHost? _host;
    private int _port;

    public LndGrpcCompatFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _port = await PortPoolUtil.GetAvailablePortAsync();
        _node = await NLightningTestNode.CreateAsync(_fixture, "lndgrpc");
        Node.ExtraConfiguration["LndGrpc:Enabled"] = "true";
        Node.ExtraConfiguration["LndGrpc:ListenAddress"] = "127.0.0.1";
        Node.ExtraConfiguration["LndGrpc:Port"] = _port.ToString();
        Node.ExtraConfiguration["LndGrpc:DataDirectory"] = _directory;
        Node.ExtraConfiguration["Node:Alias"] = "nltg-lndgrpc";
        // The hosted server the daemon's own host starts (ConfigureNltgServices), which a test node's composition
        // does not include; its files go to the absolute DataDirectory above
        Node.ConfigureServices = services => services.AddLndGrpcHost(_directory);
        await Node.StartAsync(TestContext.Current.CancellationToken);

        _host = Node.Services.GetServices<IHostedService>().OfType<LndGrpcHost>().Single();
        await _host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(_port, _host.BoundPort);
        Console.WriteLine($"LND gRPC listening on 127.0.0.1:{_host.BoundPort}, files in {_directory}");
    }

    [Fact]
    public async Task Given_OurLndGrpc_When_UsedLikeAnLndNode_Then_ItAgreesWithTheRealLndAcrossAChannelAndAPayment()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");

        // Arrange: our node, seen only through the LND client with the files it baked
        using var ours = await LndNodeConnection.ConnectAsync(Settings(LndMacaroonFiles.AdminFileName), cancellationToken: ct);
        Assert.Equal(Node.NodeIdHex, ours.LocalNodePubKey);
        Assert.Equal("nltg-lndgrpc", ours.LocalAlias);

        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var opened = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var tip = await ChainSync.WaitAllAtTipAsync(_fixture, [alice], [Node], ct);

        // Act / Assert 1: GetInfo and ListChannels against what alice reports for the same channel
        var info = await ours.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);
        Assert.Equal(tip, info.BlockHeight);
        Assert.Equal("regtest", Assert.Single(info.Chains).Network);
        Assert.Equal(1u, info.NumActiveChannels);
        Assert.True(info.NumPeers >= 1);

        var aliceChannel = (await LndTestHelpers.GetChannelByPointAsync(alice, opened.ChannelPoint(), ct))!;
        var ourChannel = await Poll.ForAsync(async () =>
        {
            var channel = await LndTestHelpers.GetChannelByPointAsync(ours, opened.ChannelPoint(), ct);
            return channel is { Active: true } ? channel : null;
        }, s_timeout, "our ListChannels reports the channel active", ct);
        Console.WriteLine($"ours: chan_id {ourChannel.ChanId} local {ourChannel.LocalBalance} remote "
                        + $"{ourChannel.RemoteBalance} fee {ourChannel.CommitFee} type {ourChannel.CommitmentType}; "
                        + $"alice: chan_id {aliceChannel.ChanId} local {aliceChannel.LocalBalance} remote "
                        + $"{aliceChannel.RemoteBalance} fee {aliceChannel.CommitFee}");
        Assert.Equal(aliceChannel.ChanId, ourChannel.ChanId);
        Assert.Equal(aliceChannel.Capacity, ourChannel.Capacity);
        Assert.Equal(alice.LocalNodePubKey, ourChannel.RemotePubkey);
        Assert.Equal(Node.NodeIdHex, aliceChannel.RemotePubkey);
        Assert.Equal(aliceChannel.CommitmentType, ourChannel.CommitmentType);
        Assert.True(ourChannel.Initiator);
        Assert.Equal(aliceChannel.RemoteBalance, ourChannel.LocalBalance);
        Assert.Equal(aliceChannel.LocalBalance, ourChannel.RemoteBalance);
        Assert.Equal(aliceChannel.CommitFee, ourChannel.CommitFee);
        Assert.Equal(s_push.Satoshi, ourChannel.RemoteBalance);

        var balanceBefore = await ours.LightningClient.ChannelBalanceAsync(new ChannelBalanceRequest(),
                                                                            cancellationToken: ct);
        Assert.Equal((ulong)ourChannel.LocalBalance, balanceBefore.LocalBalance.Sat);

        // Act / Assert 2: our AddInvoice (the LND helper, against our endpoint), alice pays it over the channel
        var invoice = await LndTestHelpers.AddInvoiceAsync(ours, InvoiceMsat, [], ct, memo: "lnd grpc compat");
        var payment = await PayAsync(alice, invoice.PaymentRequest, aliceChannel.ChanId, ct);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);

        var settled = await Poll.ForAsync(async () =>
        {
            var found = await LndTestHelpers.LookupInvoiceAsync(ours, invoice.RHash.ToByteArray(), ct);
            return found.State == Invoice.Types.InvoiceState.Settled ? found : null;
        }, s_timeout, "our LookupInvoice reports the invoice SETTLED", ct);
        Assert.Equal(InvoiceMsat, settled.AmtPaidMsat);
        Assert.Equal(payment.PaymentPreimage, Convert.ToHexStringLower(settled.RPreimage.ToByteArray()));
        Assert.Equal("lnd grpc compat", settled.Memo);
        Assert.True(settled.SettleIndex > 0);

        var listed = await ours.LightningClient.ListInvoicesAsync(
                         new ListInvoiceRequest { Reversed = true, NumMaxInvoices = 10 }, cancellationToken: ct);
        Assert.Contains(listed.Invoices, i => i.RHash == invoice.RHash && i.AddIndex == invoice.AddIndex);

        // The balances moved by the invoice on both sides, as alice sees them too
        await Poll.UntilAsync(async () =>
        {
            var mine = await LndTestHelpers.GetChannelByPointAsync(ours, opened.ChannelPoint(), ct);
            var hers = await LndTestHelpers.GetChannelByPointAsync(alice, opened.ChannelPoint(), ct);
            return mine is not null && hers is not null && mine.PendingHtlcs.Count == 0
                && mine.LocalBalance == hers.RemoteBalance && mine.RemoteBalance == hers.LocalBalance
                && mine.LocalBalance == ourChannel.LocalBalance + InvoiceMsat / 1000;
        }, s_timeout, "both sides agree on the balances after the payment", ct);
        var balanceAfter = await ours.LightningClient.ChannelBalanceAsync(new ChannelBalanceRequest(),
                                                                           cancellationToken: ct);
        Assert.Equal(balanceBefore.LocalBalance.Msat + (ulong)InvoiceMsat, balanceAfter.LocalBalance.Msat);

        // Act / Assert 3: our SignMessage is LND's: alice's VerifyMessage recovers our node key
        var signed = await ours.LightningClient.SignMessageAsync(
                         new SignMessageRequest { Msg = Google.Protobuf.ByteString.CopyFromUtf8("nltg speaks lnd") },
                         cancellationToken: ct);
        var verified = await alice.LightningClient.VerifyMessageAsync(new VerifyMessageRequest
        {
            Msg = Google.Protobuf.ByteString.CopyFromUtf8("nltg speaks lnd"),
            Signature = signed.Signature
        }, cancellationToken: ct);
        Console.WriteLine($"alice verified our signature: valid {verified.Valid}, pubkey {verified.Pubkey}");
        Assert.Equal(Node.NodeIdHex, verified.Pubkey);

        // …and alice's signature verifies on ours with her key
        var aliceSigned = await alice.LightningClient.SignMessageAsync(
                              new SignMessageRequest { Msg = Google.Protobuf.ByteString.CopyFromUtf8("lnd to nltg") },
                              cancellationToken: ct);
        var ourVerify = await ours.LightningClient.VerifyMessageAsync(new VerifyMessageRequest
        {
            Msg = Google.Protobuf.ByteString.CopyFromUtf8("lnd to nltg"),
            Signature = aliceSigned.Signature
        }, cancellationToken: ct);
        Assert.Equal(alice.LocalNodePubKey, ourVerify.Pubkey);

        // The read-only macaroon reads but cannot make invoices; a call without a macaroon is refused
        using var readOnly =
            await LndNodeConnection.ConnectAsync(Settings(LndMacaroonFiles.ReadOnlyFileName), cancellationToken: ct);
        var denied = await Assert.ThrowsAsync<RpcException>(
            () => LndTestHelpers.AddInvoiceAsync(readOnly, 1_000, [], ct));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.NotNull(await LndTestHelpers.GetChannelByPointAsync(readOnly, opened.ChannelPoint(), ct));
    }

    private LndSettings Settings(string macaroonFile) =>
        LndSettings.FromFiles($"https://127.0.0.1:{_port}", Path.Combine(_directory, LndTlsFiles.CertificateFileName),
                              Path.Combine(_directory, macaroonFile));

    /// <summary>alice pays <paramref name="bolt11"/> over the channel (NL-319: a fresh channel may not be in her
    /// router's graph yet, retried).</summary>
    private static async Task<Payment> PayAsync(LndNodeConnection alice, string bolt11, ulong chanId,
                                                CancellationToken ct)
    {
        return await Poll.ForAsync(async () =>
        {
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var payment = await LndTestHelpers.SendPaymentV2Async(alice, LndTestHelpers.PinnedPayment(bolt11, [chanId]),
                                                                  ct);
            if (payment.Status == Payment.Types.PaymentStatus.Succeeded)
                return payment;

            Console.WriteLine($"alice's payment ended {payment.Status} {payment.FailureReason}; retrying");
            return null;
        }, s_timeout, "alice paid the invoice made through our LND gRPC", ct);
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
