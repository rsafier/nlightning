using System.Collections.Concurrent;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Routerrpc;
using NLightning.Testing.Lnd.Walletrpc;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Payments.Enums;
using Domain.Protocol.ValueObjects;
using Fixtures;
using Infrastructure.Bitcoin.Managers;
using LndGrpc;
using LndGrpc.Macaroons;
using LndGrpc.Tls;
using TestCollections;
using Utils;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using ListUnspentRequest = NLightning.Testing.Lnd.Walletrpc.ListUnspentRequest;
using Transaction = NLightning.Testing.Lnd.Walletrpc.Transaction;

/// <summary>
/// NL-1168 proof of the LND gRPC wave 3 against the fixture's real LND alice, driving our node only through
/// <see cref="LndNodeConnection"/> (our <c>tls.cert</c> and <c>admin.macaroon</c>), as an LND client would:
/// <list type="number">
///   <item>ChannelAcceptor (NL-1180): with our acceptor stream connected, alice's first <c>OpenChannelSync</c> to us is
///   rejected with the acceptor's error text (LND reports it), the second is accepted and opens.</item>
///   <item>HtlcInterceptor (NL-1183): alice pays a second NLightning node through us; our interceptor sees the forward
///   (circuit key = alice's channel and HTLC id) and FAILs it, so alice's attempt fails at our hop with
///   TEMPORARY_CHANNEL_FAILURE; the next payment is RESUMEd and settles.</item>
///   <item>WalletKit and GetTransactions (NL-1184, NL-1185): ListUnspent and NextAddr over the node's real wallet;
///   FundPsbt pays alice, FinalizePsbt signs, PublishTransaction sends it; once mined, alice holds the output and
///   our GetTransactions lists the spend with its net amount.</item>
/// </list>
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public partial class LndGrpcWave3FlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private const string RejectionText = "nltg acceptor: not this one";
    private const long PaymentSat = 25_000;
    private const long PsbtSat = 77_000;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "nltg-lndgrpc3-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<NLightningTestNode> _nodes = [];
    private readonly List<SecureKeyManager> _keys = [];

    private NLightningTestNode? _node;
    private NLightningTestNode? _payee;
    private LndGrpcHost? _host;
    private int _port;

    public LndGrpcWave3FlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");
    private NLightningTestNode Payee => _payee ?? throw new InvalidOperationException("The payee was not created");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.SkipIfUnavailable();
        _port = await PortPoolUtil.GetAvailablePortAsync();
        Directory.CreateDirectory(_directory);
        var birthday = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        var nodeKeys = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, Path.Combine(_directory, "node-keys.json"), birthday);
        _keys.Add(nodeKeys);
        _node = await NLightningTestNode.CreateAsync(_fixture, "lndgrpc3", secureKeyManager: nodeKeys);
        _nodes.Add(_node);
        Node.ExtraConfiguration["SilentPayments:Enabled"] = "true";
        Node.ExtraConfiguration["LndGrpc:Enabled"] = "true";
        Node.ExtraConfiguration["LndGrpc:ListenAddress"] = "127.0.0.1";
        Node.ExtraConfiguration["LndGrpc:Port"] = _port.ToString();
        Node.ExtraConfiguration["LndGrpc:DataDirectory"] = _directory;
        Node.ExtraConfiguration["Node:Alias"] = "nltg-lndgrpc3";
        Node.ConfigureServices = services =>
        {
            services.AddLndGrpcHost(_directory);
            services.AddLogging(builder => builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
        };
        await Node.StartAsync(ct);
        _host = Node.Services.GetServices<IHostedService>().OfType<LndGrpcHost>().Single();
        await _host.StartAsync(ct);
        Console.WriteLine($"LND gRPC listening on 127.0.0.1:{_host.BoundPort}, files in {_directory}");

        var payeeKeys = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, Path.Combine(_directory, "payee-keys.json"), birthday);
        _keys.Add(payeeKeys);
        _payee = await NLightningTestNode.CreateAsync(_fixture, "lndgrpc3-payee", secureKeyManager: payeeKeys);
        _nodes.Add(_payee);
        Payee.ExtraConfiguration["SilentPayments:Enabled"] = "true";
        Payee.ConfigureServices = services =>
            services.AddLogging(builder => builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
        await Payee.StartAsync(ct);
    }

    [Fact]
    public async Task Given_OurLndGrpc_When_AnLndClientAcceptsInterceptsAndSpends_Then_LndAndOurNodeAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        using var ours = LndNodeConnection.CreateWithoutNodeInfo(Settings(LndMacaroonFiles.AdminFileName));
        await using var subscriptions = await ObserveSubscriptionsAsync(ours, ct);

        // Arrange: our wallet backs the anchors reserves; the payee keeps its own as fundee (NL-379)
        await EnsureLndWalletFundedAsync(alice, ct);
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await Payee.FundWalletAsync(LightningMoney.Satoshis(200_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice], _nodes, ct);
        await Node.ConnectToAsync(alice, ct);

        // Act / Assert 1: ChannelAcceptor rejects alice's first open with our text, accepts the second
        var aliceChanId = await AcceptorFlowAsync(ours, alice, ct);

        // A channel from us to the payee, which alice's payments to the payee cross
        await Node.ConnectToAsync(Payee, ct);
        var toPayee = await Node.OpenChannelAsync(new OpenChannelClientRequest(Payee.Address,
                                                                               LightningMoney.Satoshis(1_000_000))
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000),
            IsPublic = true
        }, ct);
        await Poll.UntilAsync(async () =>
        {
            var usable = (await Node.GetChannelAsync(toPayee.ChannelId, ct)).IsUsable()
                      && (await Payee.GetChannelAsync(toPayee.ChannelId, ct)) is { ShortChannelId: not null } theirs
                      && theirs.IsUsable()
                      && Payee.Services.GetRequiredService<Application.Gossip.Interfaces.IChannelUpdateService>()
                              .TryGetRemoteChannelUpdate(toPayee.ChannelId, out _);
            if (usable)
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);
            return false;
        }, s_timeout, "our channel to the payee usable, with our channel_update at the payee", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice], _nodes, ct);

        // Act / Assert 2: HtlcInterceptor fails the first forward, resumes the second
        await InterceptorFlowAsync(ours, alice, aliceChanId, ct);

        // Act / Assert 3: WalletKit and GetTransactions over the real wallet
        await WalletFlowAsync(ours, alice, ct);
        await subscriptions.AssertObservedAsync(this, ours, alice, toPayee.ChannelId, ct);
    }

    private async Task<ulong> AcceptorFlowAsync(LndNodeConnection ours, LndNodeConnection alice, CancellationToken ct)
    {
        using var acceptorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var acceptor = ours.LightningClient.ChannelAcceptor(cancellationToken: acceptorCts.Token);
        var seen = new ConcurrentQueue<ChannelAcceptRequest>();
        var answering = Task.Run(async () =>
        {
            while (await acceptor.ResponseStream.MoveNext(acceptorCts.Token))
            {
                var request = acceptor.ResponseStream.Current;
                var first = seen.IsEmpty;
                seen.Enqueue(request);
                await acceptor.RequestStream.WriteAsync(first
                                                            ? new ChannelAcceptResponse
                                                            {
                                                                PendingChanId = request.PendingChanId,
                                                                Accept = false,
                                                                Error = RejectionText
                                                            }
                                                            : new ChannelAcceptResponse
                                                            {
                                                                PendingChanId = request.PendingChanId,
                                                                Accept = true,
                                                                CsvDelay = 200
                                                            }, acceptorCts.Token);
            }
        }, CancellationToken.None);
        await Poll.UntilAsync(() => Task.FromResult(Node.Services
                                                        .GetRequiredService<Domain.Channels.Acceptance.
                                                             IChannelOpenDecisionGate>()
                                                        .HasDeciders), s_timeout, "our acceptor registered", ct);

        var refusal = await Assert.ThrowsAsync<RpcException>(async () => await OpenFromAliceAsync(alice, ct));
        Console.WriteLine($"alice's first open failed: {refusal.Status.Detail}");
        Assert.Contains(RejectionText, refusal.Status.Detail);
        var request = Assert.Single(seen);
        Assert.Equal(alice.LocalNodePubKey, Convert.ToHexStringLower(request.NodePubkey.ToByteArray()));
        Assert.Equal(500_000UL, request.FundingAmt);
        Assert.Equal(CommitmentType.Anchors, request.CommitmentType);

        // Our error for the temporary channel closes the connection: wait for that, then connect again
        await ReconnectAsync(alice, ct);
        var point = await OpenFromAliceAsync(alice, ct);
        Console.WriteLine($"alice's second open: {Convert.ToHexString(point.FundingTxidBytes.ToByteArray())}:"
                        + $"{point.OutputIndex}");
        Assert.Equal(2, seen.Count);
        acceptorCts.Cancel();
        try
        {
            await answering;
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException)
        {
            // The stream ended with the cancellation
        }

        var aliceChannel = await Poll.ForAsync(async () =>
        {
            var channels = await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest
            {
                ActiveOnly = true,
                Peer = ByteString.CopyFrom(Convert.FromHexString(Node.NodeIdHex))
            }, cancellationToken: ct);
            var found = channels.Channels.FirstOrDefault(c => c.Initiator);
            if (found is not null)
                return found;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);
            return null;
        }, s_timeout, "alice's channel to us active", ct);

        // The acceptor's csv_delay is what we announced: alice's to_self_delay on her own outputs
        Assert.Equal(200U, aliceChannel.LocalConstraints.CsvDelay);
        await Poll.UntilAsync(async () => await LndTestHelpers.GetChannelByPointAsync(ours, aliceChannel.ChannelPoint, ct)
                                              is { Active: true }, s_timeout, "our end of alice's channel active", ct);
        return aliceChannel.ChanId;
    }

    private async Task InterceptorFlowAsync(LndNodeConnection ours, LndNodeConnection alice, ulong aliceChanId,
                                            CancellationToken ct)
    {
        using var interceptorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var interceptor = ours.RouterClient.HtlcInterceptor(cancellationToken: interceptorCts.Token);
        var mode = (int)ResolveHoldForwardAction.Fail;
        var seen = new ConcurrentQueue<ForwardHtlcInterceptRequest>();
        var answering = Task.Run(async () =>
        {
            while (await interceptor.ResponseStream.MoveNext(interceptorCts.Token))
            {
                var request = interceptor.ResponseStream.Current;
                seen.Enqueue(request);
                await interceptor.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
                {
                    IncomingCircuitKey = request.IncomingCircuitKey,
                    Action = (ResolveHoldForwardAction)Volatile.Read(ref mode)
                }, interceptorCts.Token);
            }
        }, CancellationToken.None);
        var hub = Node.Services.GetRequiredService<Application.Payments.Interception.HtlcInterceptorHub>();
        await Poll.UntilAsync(() => Task.FromResult(hub.IsActive), s_timeout, "our interceptor connected", ct);

        // FAIL: alice's attempt fails at our hop
        var failing = await Payee.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), "intercepted: fail", ct);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var failed = await LndTestHelpers.SendPaymentV2Async(alice, LndTestHelpers.PinnedPayment(failing.Bolt11!,
                                                                                                  [aliceChanId]), ct);
        Console.WriteLine($"alice's first payment: {failed.Status} {failed.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Failed, failed.Status);
        var attempt = Assert.Single(failed.Htlcs.Where(h => h.Failure is not null).Take(1));
        Assert.Equal(Failure.Types.FailureCode.TemporaryChannelFailure, attempt.Failure.Code);
        Assert.Equal(1U, attempt.Failure.FailureSourceIndex);
        var held = Assert.Single(seen.Take(1));
        Assert.Equal(aliceChanId, held.IncomingCircuitKey.ChanId);
        Assert.Equal((ulong)PaymentSat * 1_000, held.OutgoingAmountMsat);
        Assert.Equal((byte[])failing.PaymentHash, held.PaymentHash.ToByteArray());
        Assert.Equal(1366, held.OnionBlob.Length);
        Assert.Equal(InvoiceStatus.Open, (await Payee.GetInvoiceAsync(failing.PaymentHash, ct))!.Status);

        // RESUME: the next forward goes on and settles at the payee
        Volatile.Write(ref mode, (int)ResolveHoldForwardAction.Resume);
        var seenBefore = seen.Count;
        var resumed = await Payee.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), "intercepted: resume", ct);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var paid = await LndTestHelpers.SendPaymentV2Async(alice, LndTestHelpers.PinnedPayment(resumed.Bolt11!,
                                                                                                [aliceChanId]), ct);
        Console.WriteLine($"alice's second payment: {paid.Status} {paid.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, paid.Status);
        Assert.True(seen.Count > seenBefore, "the resumed forward went through the interceptor");
        await Poll.UntilAsync(async () => (await Payee.GetInvoiceAsync(resumed.PaymentHash, ct))?.Status
                                       == InvoiceStatus.Settled, s_timeout, "the payee's invoice settled", ct);

        interceptorCts.Cancel();
        try
        {
            await answering;
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException)
        {
            // The stream ended with the cancellation
        }

        await Poll.UntilAsync(() => Task.FromResult(!hub.IsActive), s_timeout, "our interceptor gone", ct);
    }

    private async Task WalletFlowAsync(LndNodeConnection ours, LndNodeConnection alice, CancellationToken ct)
    {
        // ListUnspent and NextAddr over the node's wallet
        var unspent = await ours.WalletKitClient.ListUnspentAsync(new ListUnspentRequest { MinConfs = 1 },
                                                                  cancellationToken: ct);
        Console.WriteLine($"ListUnspent: {unspent.Utxos.Count} output(s), {unspent.Utxos.Sum(u => u.AmountSat)} sat");
        Assert.NotEmpty(unspent.Utxos);
        Assert.All(unspent.Utxos, u => Assert.True(u.Confirmations >= 1));
        var next = await ours.WalletKitClient.NextAddrAsync(new AddrRequest(), cancellationToken: ct);
        Assert.Equal(Network.RegTest, BitcoinAddress.Create(next.Addr, Network.RegTest).Network);

        // FundPsbt to alice, FinalizePsbt, PublishTransaction
        var aliceAddress = await alice.LightningClient.NewAddressAsync(new NewAddressRequest
        {
            Type = Testing.Lnd.Lnrpc.AddressType.WitnessPubkeyHash
        }, cancellationToken: ct);
        var fund = new FundPsbtRequest { Raw = new TxTemplate(), SatPerVbyte = 5 };
        fund.Raw.Outputs[aliceAddress.Address] = PsbtSat;
        var funded = await ours.WalletKitClient.FundPsbtAsync(fund, cancellationToken: ct);
        Assert.NotEmpty(funded.LockedUtxos);
        var leases = await ours.WalletKitClient.ListLeasesAsync(new ListLeasesRequest(), cancellationToken: ct);
        Assert.Equal(funded.LockedUtxos.Count, leases.LockedUtxos.Count);
        var finalized = await ours.WalletKitClient.FinalizePsbtAsync(new FinalizePsbtRequest
        {
            FundedPsbt = funded.FundedPsbt
        }, cancellationToken: ct);
        var tx = NBitcoin.Transaction.Load(finalized.RawFinalTx.ToByteArray(), Network.RegTest);
        var published = await ours.WalletKitClient.PublishTransactionAsync(new Transaction
        {
            TxHex = finalized.RawFinalTx,
            Label = "wave 3 psbt"
        }, cancellationToken: ct);
        Console.WriteLine($"Published {tx.GetHash()}: '{published.PublishError}'");
        Assert.Equal("", published.PublishError);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);

        // alice holds the output; our history lists the spend with its net amount (the amount and the fee)
        var txHash = tx.GetHash().ToString();
        var aliceSees = await Poll.ForAsync(async () =>
        {
            var history = await alice.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                           cancellationToken: ct);
            return history.Transactions.FirstOrDefault(t => t.TxHash == txHash && t.NumConfirmations >= 1);
        }, s_timeout, "alice sees our PSBT spend confirmed", ct);
        Assert.Equal(PsbtSat, aliceSees.Amount);
        var oursSees = await Poll.ForAsync(async () =>
        {
            var history = await ours.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                          cancellationToken: ct);
            return history.Transactions.FirstOrDefault(t => t.TxHash == txHash && t.NumConfirmations >= 1);
        }, s_timeout, "our GetTransactions lists the PSBT spend confirmed", ct);
        Console.WriteLine($"our history: {oursSees.TxHash} amount {oursSees.Amount} fee {oursSees.TotalFees} "
                        + $"label '{oursSees.Label}' at {oursSees.BlockHeight}");
        Assert.True(oursSees.TotalFees > 0);
        Assert.Equal(-(PsbtSat + oursSees.TotalFees), oursSees.Amount);
        Assert.Equal("wave 3 psbt", oursSees.Label);
        Assert.Equal(aliceSees.BlockHeight, oursSees.BlockHeight);

        // The deposit that funded the wallet is in the history too
        var history = await ours.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                      cancellationToken: ct);
        Assert.Contains(history.Transactions, t => t.Amount > 0 && t.NumConfirmations >= 1);
    }

    private async Task<ChannelPoint> OpenFromAliceAsync(LndNodeConnection alice, CancellationToken ct) =>
        await alice.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom(Convert.FromHexString(Node.NodeIdHex)),
            LocalFundingAmount = 500_000,
            Private = true,
            CommitmentType = CommitmentType.Anchors
        }, cancellationToken: ct).ResponseAsync.WaitAsync(s_timeout, ct);

    private async Task ReconnectAsync(LndNodeConnection alice, CancellationToken ct)
    {
        var aliceId = new CompactPubKey(alice.LocalNodePubKeyBytes);
        var peers = Node.Services.GetRequiredService<IPeerManager>();
        for (var i = 0; i < 20 && peers.GetPeer(aliceId) is not null; i++)
            await Task.Delay(500, ct);

        if (peers.GetPeer(aliceId) is null)
            await Node.ConnectToAsync(alice, ct);
    }

    private async Task EnsureLndWalletFundedAsync(LndNodeConnection lnd, CancellationToken ct)
    {
        var balance = await lnd.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(), cancellationToken: ct);
        if (balance.ConfirmedBalance >= 1_000_000)
            return;

        var address = await lnd.LightningClient.NewAddressAsync(new NewAddressRequest
        {
            Type = Testing.Lnd.Lnrpc.AddressType.WitnessPubkeyHash
        }, cancellationToken: ct);
        await _fixture.Bitcoin.SendToAddressAsync(BitcoinAddress.Create(address.Address, Network.RegTest),
                                                  Money.Coins(0.05m), cancellationToken: ct);
        await ChainSync.MineAndWaitAsync(_fixture, 6, [lnd], _nodes, ct);
        await Poll.UntilAsync(async () => (await lnd.LightningClient.WalletBalanceAsync(
                                               new WalletBalanceRequest(), cancellationToken: ct)).ConfirmedBalance
                                        >= 1_000_000, s_timeout, $"{lnd.LocalAlias}'s wallet funded", ct);
    }

    private LndSettings Settings(string macaroonFile) =>
        LndSettings.FromFiles($"https://127.0.0.1:{_port}", Path.Combine(_directory, LndTlsFiles.CertificateFileName),
                              Path.Combine(_directory, macaroonFile));

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            await _fixture.DumpLndLogsAsync(["alice"]);
        }

        if (_host is not null)
            await _host.StopAsync(CancellationToken.None);
        foreach (var node in _nodes)
            await node.DisposeAsync();
        foreach (var keys in _keys)
            keys.Dispose();
        PortPoolUtil.ReleasePort(_port);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
        GC.SuppressFinalize(this);
    }
}