using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NBitcoin;

namespace NLightning.LndGrpc.Tests;

using Application.Payments.Events;
using Application.Payments.Interception;
using Domain.Accounting.Labels;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Google.Protobuf;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Repositories.Memory;
using LndGrpc.Macaroons;
using LndGrpc.Services;
using LndGrpc.Tls;
using Testing.Lnd;
using Testing.Lnd.Lnrpc;
using Invoice = Testing.Lnd.Lnrpc.Invoice;
using MacaroonId = LndGrpc.Macaroons.MacaroonId;

/// <summary>
/// The LND gRPC server (NL-1161) on a real loopback TLS listener, driven by our in-tree LND client
/// (<see cref="LndNodeConnection"/>, the one the cluster suites use against real LND 0.21.4) with the macaroons and
/// the certificate the host made, like an LND client pointed at an LND node. The node's services are mocked.
/// </summary>
public sealed partial class LndGrpcHostTests : IAsyncLifetime
{
    private static readonly byte[] s_nodeKey =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

    private const string NodeIdHex = "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-lndgrpc-" + Guid.NewGuid().ToString("N"));
    private readonly List<ChannelModel> _channels = [];
    private readonly List<InvoiceModel> _invoices = [];
    private readonly List<PaymentModel> _payments = [];
    private readonly List<ForwardCircuitModel> _forwards = [];
    private readonly Mock<IInvoiceService> _invoiceService = new();
    private readonly Mock<IPeerManager> _peers = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemory = new();
    private readonly Mock<IPaymentService> _paymentService = new();
    private readonly Mock<IHoldInvoiceService> _holdInvoices = new();
    private readonly Mock<IBitcoinWalletService> _wallet = new();
    private readonly Mock<IWalletSpendService> _walletSpend = new();
    private readonly Mock<IFeeService> _fees = new();
    private readonly PaymentEventHub _events = new();
    private readonly Mock<Domain.Channels.RoutingPolicies.IChannelPolicyService> _policies = new();
    private readonly FakeDispatcher _dispatcher = new();
    private readonly List<ChannelModel> _closedChannels = [];
    private readonly List<OutputResolutionModel> _outputs = [];
    private readonly List<ChannelCloseModel> _closes = [];
    private readonly UtxoMemoryRepository _utxos = new();

    private ServiceProvider? _services;
    private LndGrpcHost? _host;

    public async ValueTask InitializeAsync()
    {
        _services = BuildServices();
        _host = new LndGrpcHost(_services,
                                Options.Create(new LndGrpcOptions { Enabled = true, Port = 0 }), _directory,
                                NullLogger<LndGrpcHost>.Instance);
        await _host.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.StopAsync(CancellationToken.None);
        if (_services is not null)
            await _services.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Given_TheHost_When_Started_Then_ItWroteTheCertificateTheRootKeyAndThreeMacaroonsOwnerOnly()
    {
        // Act
        var files = new[]
        {
            LndTlsFiles.CertificateFileName, LndTlsFiles.KeyFileName, LndMacaroonFiles.RootKeyFileName,
            LndMacaroonFiles.AdminFileName, LndMacaroonFiles.ReadOnlyFileName, LndMacaroonFiles.InvoiceFileName
        }.Select(f => Path.Combine(_directory, f)).ToList();
        var admin = await File.ReadAllBytesAsync(Path.Combine(_directory, LndMacaroonFiles.AdminFileName), Ct);

        // Assert
        Assert.All(files, f => Assert.True(File.Exists(f), f));
        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in files)
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        var macaroon = Macaroon.Deserialize(admin);
        Assert.Equal("lnd", macaroon.Location);
        Assert.Equal(MacaroonId.Canonical(LndPermissions.Admin), MacaroonId.Decode(macaroon.Identifier).Ops);
    }

    [Fact]
    public async Task Given_ARestart_When_TheFilesExist_Then_TheyAreKeptAndOldMacaroonsStillWork()
    {
        // Arrange
        var adminPath = Path.Combine(_directory, LndMacaroonFiles.AdminFileName);
        var before = await File.ReadAllBytesAsync(adminPath, Ct);
        var certificate = await File.ReadAllBytesAsync(Path.Combine(_directory, LndTlsFiles.CertificateFileName), Ct);

        // Act
        await _host!.StopAsync(Ct);
        _host = new LndGrpcHost(_services!, Options.Create(new LndGrpcOptions { Enabled = true, Port = 0 }),
                                _directory, NullLogger<LndGrpcHost>.Instance);
        await _host.StartAsync(Ct);
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Assert
        Assert.Equal(before, await File.ReadAllBytesAsync(adminPath, Ct));
        Assert.Equal(certificate,
                     await File.ReadAllBytesAsync(Path.Combine(_directory, LndTlsFiles.CertificateFileName), Ct));
        Assert.Equal(NodeIdHex, connection.LocalNodePubKey);
    }

    [Fact]
    public async Task Given_TheAdminMacaroon_When_GetInfo_Then_OurIdentityChainAndCountsComeBack()
    {
        // Arrange
        _channels.Add(CreateChannel(1, ChannelState.Open));
        _channels.Add(CreateChannel(2, ChannelState.V1FundingSigned));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var info = await connection.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct);

        // Assert
        Assert.Equal(NodeIdHex, info.IdentityPubkey);
        Assert.Equal("nltg-test", info.Alias);
        Assert.Equal("#3399ff", info.Color);
        Assert.Equal(150u, info.BlockHeight);
        Assert.False(info.SyncedToChain);
        Assert.Equal(new string('0', 64), info.BlockHash);
        Assert.StartsWith("0.21.4-beta nlightning-", info.Version);
        Assert.Equal("regtest", Assert.Single(info.Chains).Network);
        Assert.Equal(1u, info.NumInactiveChannels);
        Assert.Equal(1u, info.NumPendingChannels);
        Assert.Contains(info.Features, f => f.Value.Name == "payment-addr");
    }

    [Fact]
    public async Task Given_NoOrAForeignMacaroon_When_Called_Then_Unauthenticated()
    {
        // Arrange
        var certificate = await File.ReadAllBytesAsync(Path.Combine(_directory, LndTlsFiles.CertificateFileName), Ct);
        var foreign = LndMacaroonFiles.NewMacaroon(new byte[32], LndPermissions.Admin).Serialize();
        using var none = LndNodeConnection.CreateWithoutNodeInfo(LndSettings.FromBytes(Endpoint, certificate, null));
        using var forged =
            LndNodeConnection.CreateWithoutNodeInfo(LndSettings.FromBytes(Endpoint, certificate, foreign));

        // Act
        var noneError = await Assert.ThrowsAsync<RpcException>(
            () => none.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct).ResponseAsync);
        var forgedError = await Assert.ThrowsAsync<RpcException>(
            () => forged.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal(StatusCode.Unauthenticated, noneError.StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, forgedError.StatusCode);
    }

    [Fact]
    public async Task Given_TheReadOnlyMacaroon_When_Writing_Then_PermissionDeniedButReadsWork()
    {
        // Arrange: at height 150, 70,000 sat mined at 140 and 5,000 sat not mined yet
        AddUtxo(70_000, 140);
        AddUtxo(5_000, 0);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var add = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.AddInvoiceAsync(new Invoice { Value = 1 }, cancellationToken: Ct)
                            .ResponseAsync);
        var sign = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.SignMessageAsync(new SignMessageRequest(), cancellationToken: Ct)
                            .ResponseAsync);
        var balance = await connection.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(),
                                                                           cancellationToken: Ct);

        // Assert
        Assert.Equal(StatusCode.PermissionDenied, add.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, sign.StatusCode);
        Assert.Equal(70_000, balance.ConfirmedBalance);
        Assert.Equal(5_000, balance.UnconfirmedBalance);
        Assert.Equal(75_000, balance.TotalBalance);
        Assert.Equal(70_000, balance.AccountBalance["default"].ConfirmedBalance);
    }

    [Fact]
    public async Task Given_OutputsWith0And1And4Confirmations_When_WalletBalance_Then_ConfirmedFromOneConfirmationLikeLnd()
    {
        // Arrange: at height 150, 1,000 sat in the mempool, 20,000 sat mined at the tip (1 confirmation) and 300,000 sat
        // mined at 147 (4 confirmations); the node's own walletbalance confirms only the last one (NL-1236)
        AddUtxo(1_000, 0);
        AddUtxo(20_000, 150);
        AddUtxo(300_000, 147);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var balance = await connection.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(),
                                                                           cancellationToken: Ct);
        var fourConfirmations = await connection.LightningClient.WalletBalanceAsync(
                                    new WalletBalanceRequest { MinConfs = 4 }, cancellationToken: Ct);

        // Assert
        Assert.Equal(320_000, balance.ConfirmedBalance);
        Assert.Equal(1_000, balance.UnconfirmedBalance);
        Assert.Equal(321_000, balance.TotalBalance);
        Assert.Equal(0, balance.LockedBalance);
        Assert.Equal(320_000, balance.AccountBalance["default"].ConfirmedBalance);
        Assert.Equal(1_000, balance.AccountBalance["default"].UnconfirmedBalance);
        Assert.Equal(300_000, fourConfirmations.ConfirmedBalance);
        Assert.Equal(21_000, fourConfirmations.UnconfirmedBalance);
        Assert.Equal(321_000, fourConfirmations.TotalBalance);
        // The node's own 4-confirmation rule is unchanged: the output mined at the tip is still unconfirmed there
        Assert.Equal(LightningMoney.Satoshis(20_000), _utxos.GetUnconfirmedBalance(150));
    }

    [Fact]
    public async Task Given_TheInvoiceMacaroon_When_AddInvoiceThenLookup_Then_TheInvoiceComesBackOpen()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.InvoiceFileName);

        // Act
        var added = await connection.LightningClient.AddInvoiceAsync(
                        new Invoice { ValueMsat = 25_000_000, Memo = "coffee", Expiry = 600 }, cancellationToken: Ct);
        var looked = await connection.LightningClient.LookupInvoiceAsync(new PaymentHash { RHash = added.RHash },
                                                                          cancellationToken: Ct);
        var info = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct).ResponseAsync);

        // Assert
        _invoiceService.Verify(x => x.CreateInvoiceAsync(LightningMoney.MilliSatoshis(25_000_000), "coffee", 600u,
                                                         It.Is<SourceLabels>(l => l.Label == "lnd-grpc"),
                                                         It.IsAny<CancellationToken>()));
        Assert.Equal(added.PaymentRequest, looked.PaymentRequest);
        Assert.Equal(25_000, looked.Value);
        Assert.Equal(Invoice.Types.InvoiceState.Open, looked.State);
        Assert.Equal(added.AddIndex, looked.AddIndex);
        Assert.Equal(added.PaymentAddr, looked.PaymentAddr);
        Assert.Equal(StatusCode.PermissionDenied, info.StatusCode);
    }

    [Fact]
    public async Task Given_AnInvoiceRequestWeCannotHonour_When_AddInvoice_Then_Refused()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var preimage = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.AddInvoiceAsync(new Invoice { RPreimage = ByteString.CopyFrom(new byte[31]) },
                                                             cancellationToken: Ct).ResponseAsync);
        var both = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.AddInvoiceAsync(new Invoice { Value = 1, ValueMsat = 2 },
                                                             cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, preimage.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, both.StatusCode);
    }

    [Fact]
    public async Task Given_FiveInvoices_When_ListedForwardAndReversed_Then_LndsPagingHolds()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
            _invoices.Add(CreateInvoice((byte)(i + 1), start.AddMinutes(i),
                                        i == 2 ? InvoiceStatus.Settled : InvoiceStatus.Open));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var first = await connection.LightningClient.ListInvoicesAsync(
                        new ListInvoiceRequest { NumMaxInvoices = 2 }, cancellationToken: Ct);
        var next = await connection.LightningClient.ListInvoicesAsync(
                       new ListInvoiceRequest { NumMaxInvoices = 2, IndexOffset = first.LastIndexOffset },
                       cancellationToken: Ct);
        var latest = await connection.LightningClient.ListInvoicesAsync(
                         new ListInvoiceRequest { NumMaxInvoices = 2, Reversed = true }, cancellationToken: Ct);
        var pending = await connection.LightningClient.ListInvoicesAsync(
                          new ListInvoiceRequest { PendingOnly = true }, cancellationToken: Ct);

        // Assert
        Assert.Equal([1, 2], first.Invoices.Select(i => (int)i.RHash[0]));
        Assert.Equal([3, 4], next.Invoices.Select(i => (int)i.RHash[0]));
        Assert.Equal(Invoice.Types.InvoiceState.Settled, next.Invoices[0].State);
        Assert.Equal([4, 5], latest.Invoices.Select(i => (int)i.RHash[0]));
        Assert.Equal(latest.Invoices[0].AddIndex, latest.FirstIndexOffset);
        Assert.Equal(4, pending.Invoices.Count);
        Assert.True(first.Invoices[0].AddIndex < first.Invoices[1].AddIndex);
    }

    [Fact]
    public async Task Given_AnOpenChannel_When_ListChannelsAndChannelBalance_Then_LndFieldsAreFilled()
    {
        // Arrange
        _channels.Add(CreateChannel(7, ChannelState.Open));
        _channels.Add(CreateChannel(8, ChannelState.V1FundingSigned));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var list = await connection.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: Ct);
        var balance = await connection.LightningClient.ChannelBalanceAsync(new ChannelBalanceRequest(),
                                                                            cancellationToken: Ct);
        var pending = await connection.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                             cancellationToken: Ct);

        // Assert
        var channel = Assert.Single(list.Channels);
        Assert.Equal(new ShortChannelId(150, 7, 0), new ShortChannelId(channel.ChanId));
        Assert.Equal(1_000_000, channel.Capacity);
        Assert.Equal(600_000, channel.LocalBalance);
        Assert.Equal(400_000, channel.RemoteBalance);
        Assert.False(channel.Active);
        Assert.True(channel.Private);
        Assert.Equal(CreatePubKey(7).ToString(), channel.RemotePubkey);
        Assert.EndsWith(":0", channel.ChannelPoint);
        Assert.Equal(600_000ul, balance.LocalBalance.Sat);
        Assert.Equal(400_000_000ul, balance.RemoteBalance.Msat);
        Assert.Equal(600_000ul, balance.PendingOpenLocalBalance.Sat);
        Assert.Single(pending.PendingOpenChannels);
    }

    [Fact]
    public async Task Given_AMessage_When_SignedAndVerified_Then_ItIsLndsSignatureAndOurKey()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var signed = await connection.LightningClient.SignMessageAsync(
                         new SignMessageRequest { Msg = ByteString.CopyFromUtf8("hi") }, cancellationToken: Ct);
        var verified = await connection.LightningClient.VerifyMessageAsync(
                           new VerifyMessageRequest { Msg = ByteString.CopyFromUtf8("hi"), Signature = signed.Signature },
                           cancellationToken: Ct);
        var stranger = await connection.LightningClient.VerifyMessageAsync(
                           new VerifyMessageRequest
                           {
                               Msg = ByteString.CopyFromUtf8("hi"),
                               Signature = "rnrphcjswusbacjnmmmrynh9pqip7sy5cx695h6mfu64iac6qmcmsd8xnsyczwmpqp9shqkth3h4jmkgyqu5z47jfn1q7gpxtaqpx4xg"
                           }, cancellationToken: Ct);

        // Assert: the signer key 11..11 signing "hi" with LND's code path (scripts/lnd-grpc/signmessage-vectors)
        Assert.Equal("ry5xpgpx9bf8nc3pwzy61ynimgarguwj8pfo6temmey5ms4mtjo16m5cried7yoxmu9sbtiacmwjkdjyadt4ts5a8w69dccxrsjfkttt",
                     signed.Signature);
        Assert.True(verified.Valid);
        Assert.Equal(NodeIdHex, verified.Pubkey);
        Assert.False(stranger.Valid);
        Assert.Equal("02de60d194e1ca5947b59fe8e2efd6aadeabfb67f2e89e13ae1a799c1e08e4a43b", stranger.Pubkey);
    }

    [Fact]
    public async Task Given_ARegtestInvoice_When_DecodePayReq_Then_ItsFieldsComeBack()
    {
        // Arrange
        using var key = new Key(s_nodeKey);
        var hash = new uint256(Enumerable.Repeat((byte)0xAB, 32).ToArray());
        var secret = new uint256(Enumerable.Repeat((byte)0xCD, 32).ToArray());
        var bolt11 = new Bolt11.Models.Invoice(LightningMoney.Satoshis(1_234), "decode me", hash, secret,
                                               BitcoinNetwork.Regtest).Encode(key);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var decoded = await connection.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = bolt11 },
                                                                          cancellationToken: Ct);
        var bad = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = "lnbc1garbage" },
                                                               cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal(NodeIdHex, decoded.Destination);
        Assert.Equal(1_234, decoded.NumSatoshis);
        Assert.Equal(1_234_000, decoded.NumMsat);
        Assert.Equal("decode me", decoded.Description);
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 32)), decoded.PaymentHash);
        Assert.Equal(Enumerable.Repeat((byte)0xCD, 32).ToArray(), decoded.PaymentAddr.ToByteArray());
        Assert.Equal(StatusCode.InvalidArgument, bad.StatusCode);
    }

    [Fact]
    public async Task Given_PaymentsAndForwards_When_Listed_Then_LndsShapesComeBack()
    {
        // Arrange
        var created = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var payee = CreatePubKey(9);
        var succeeded = new PaymentModel(new Hash(Enumerable.Repeat((byte)1, 32).ToArray()), "lnbcrt1", payee,
                                         LightningMoney.Satoshis(10_000), LightningMoney.MilliSatoshis(1_500), created,
                                         [
                                             new PaymentHop(CreatePubKey(7), new ShortChannelId(150, 7, 0),
                                                            LightningMoney.MilliSatoshis(10_001_500), 200,
                                                            new Secret(new byte[32])),
                                             new PaymentHop(payee, new ShortChannelId(160, 1, 1),
                                                            LightningMoney.Satoshis(10_000), 180,
                                                            new Secret(new byte[32]))
                                         ]);
        succeeded.Succeed(new Secret(Enumerable.Repeat((byte)2, 32).ToArray()), created.AddSeconds(3));
        succeeded.PaymentIndex = 1;
        var inFlight = new PaymentModel(new Hash(Enumerable.Repeat((byte)3, 32).ToArray()), "lnbcrt2", payee,
                                        LightningMoney.Satoshis(5), LightningMoney.Zero, created.AddMinutes(1))
        {
            PaymentIndex = 2
        };
        _payments.AddRange([succeeded, inFlight]);
        _channels.Add(CreateChannel(7, ChannelState.Open));
        var forward = ForwardCircuitModel.Restore(_channels[0].ChannelId, 4, LightningMoney.MilliSatoshis(2_001_000),
                                                  300, new Hash(new byte[32]), new Secret(new byte[32]),
                                                  new ShortChannelId(170, 2, 0), LightningMoney.Satoshis(2_000), 250,
                                                  created, ForwardCircuitStatus.Fulfilled,
                                                  new ChannelId(Enumerable.Repeat((byte)0x70, 32).ToArray()), 9,
                                                  created.AddSeconds(2));
        _forwards.Add(forward);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var complete = await connection.LightningClient.ListPaymentsAsync(
                           new ListPaymentsRequest { CountTotalPayments = true }, cancellationToken: Ct);
        var all = await connection.LightningClient.ListPaymentsAsync(
                      new ListPaymentsRequest { IncludeIncomplete = true }, cancellationToken: Ct);
        var history = await connection.LightningClient.ForwardingHistoryAsync(new ForwardingHistoryRequest(),
                                                                               cancellationToken: Ct);

        // Assert
        var payment = Assert.Single(complete.Payments);
        Assert.Equal(2ul, complete.TotalNumPayments);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(10_000, payment.ValueSat);
        Assert.Equal(1_500, payment.FeeMsat);
        Assert.Equal(string.Concat(Enumerable.Repeat("02", 32)), payment.PaymentPreimage);
        var hops = Assert.Single(payment.Htlcs).Route.Hops;
        Assert.Equal(1_500, hops[0].FeeMsat);
        Assert.Equal(10_000_000, hops[0].AmtToForwardMsat);
        Assert.Equal(new ShortChannelId(160, 1, 1), new ShortChannelId(hops[1].ChanId));
        Assert.Equal(2, all.Payments.Count);
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, all.Payments[1].Status);
        var hop = Assert.Single(history.ForwardingEvents);
        Assert.Equal(new ShortChannelId(150, 7, 0), new ShortChannelId(hop.ChanIdIn));
        Assert.Equal(new ShortChannelId(170, 2, 0), new ShortChannelId(hop.ChanIdOut));
        Assert.Equal(1_000ul, hop.FeeMsat);
        Assert.Equal(1u, history.LastOffsetIndex);
    }

    [Fact]
    public async Task Given_TheAdminMacaroon_When_CallingAWave2Method_Then_Unimplemented()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.EstimateFeeAsync(new EstimateFeeRequest(), cancellationToken: Ct)
                            .ResponseAsync);
        var graph = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.DescribeGraphAsync(new ChannelGraphRequest(), cancellationToken: Ct)
                            .ResponseAsync);
        var self = await connection.LightningClient.GetNodeInfoAsync(new NodeInfoRequest { PubKey = NodeIdHex },
                                                                      cancellationToken: Ct);

        // Assert
        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
        Assert.Equal(StatusCode.Unavailable, graph.StatusCode);
        Assert.Equal("nltg-test", self.Node.Alias);
    }

    private string Endpoint => $"https://127.0.0.1:{_host!.BoundPort}";

    /// <summary>A connection with one of the baked macaroons; <c>GetInfo</c> is called unless the macaroon cannot
    /// (the invoice macaroon has no <c>info:read</c>, as in LND).</summary>
    private void AddUtxo(long satoshis, uint blockHeight) =>
        _utxos.Add(new UtxoModel(new TxId(RandomNumberGenerator.GetBytes(32)), 0, LightningMoney.Satoshis(satoshis),
                                 blockHeight, 0, false, Domain.Bitcoin.Enums.AddressType.P2Wpkh));

    private async Task<LndNodeConnection> ConnectAsync(string macaroonFile)
    {
        var settings = LndSettings.FromFiles(Endpoint, Path.Combine(_directory, LndTlsFiles.CertificateFileName),
                                             Path.Combine(_directory, macaroonFile));
        return macaroonFile == LndMacaroonFiles.InvoiceFileName
                   ? LndNodeConnection.CreateWithoutNodeInfo(settings)
                   : await LndNodeConnection.ConnectAsync(settings, cancellationToken: Ct);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions
        {
            BitcoinNetwork = BitcoinNetwork.Regtest,
            Alias = "nltg-test"
        }));

        var keyManager = new Mock<ISecureKeyManager>();
        var nodeId = new CompactPubKey(Convert.FromHexString(NodeIdHex));
        keyManager.Setup(x => x.GetNodeKeyPair()).Returns(() => new CryptoKeyPair(s_nodeKey.ToArray(), nodeId));
        keyManager.Setup(x => x.GetNodePubKey()).Returns(nodeId);
        var signer = new LocalLightningSigner(new Mock<IFundingOutputBuilder>().Object,
                                              new Mock<IKeyDerivationService>().Object,
                                              NullLogger<LocalLightningSigner>.Instance,
                                              new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest },
                                              keyManager.Object, new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton<ILightningSigner>(signer);

        _channelMemory.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                      .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        services.AddSingleton(_channelMemory.Object);

        _peers.Setup(x => x.ListPeers()).Returns([]);
        services.AddSingleton(_peers.Object);

        _invoiceService.Setup(x => x.CreateInvoiceAsync(It.IsAny<LightningMoney?>(), It.IsAny<string>(),
                                                        It.IsAny<uint?>(), It.IsAny<SourceLabels>(),
                                                        It.IsAny<CancellationToken>()))
                       .ReturnsAsync((LightningMoney? amount, string description, uint? expiry, SourceLabels _,
                                      CancellationToken _) =>
                       {
                           var invoice = new InvoiceModel(new Hash(Enumerable.Repeat((byte)0x42, 32).ToArray()),
                                                          new Secret(new byte[32]),
                                                          new Secret(Enumerable.Repeat((byte)0x24, 32).ToArray()),
                                                          amount, description, "lnbcrt250u1fake",
                                                          DateTimeOffset.UtcNow, expiry ?? 3600, 40)
                           {
                               AddIndex = (ulong)_invoices.Count + 1
                           };
                           _invoices.Add(invoice);
                           return invoice;
                       });
        _invoiceService.Setup(x => x.GetInvoiceAsync(It.IsAny<Hash>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((Hash hash, CancellationToken _) =>
                                         _invoices.FirstOrDefault(i => i.PaymentHash == hash));
        services.AddSingleton(_invoiceService.Object);

        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(x => x.LastProcessedBlockHeight).Returns(150);
        services.AddSingleton(monitor.Object);

        services.AddSingleton<IUtxoMemoryRepository>(_utxos);

        services.AddScoped(_ => CreateUnitOfWork());
        services.AddScoped(_ => _wallet.Object);
        services.AddSingleton(_walletSpend.Object);
        services.AddSingleton(_fees.Object);
        services.AddSingleton(_paymentService.Object);
        services.AddSingleton(_holdInvoices.Object);
        services.AddSingleton<IPaymentEventSource>(_events);
        services.AddSingleton(_policies.Object);
        services.AddSingleton<INodeCommandDispatcher>(_dispatcher);
        services.AddSingleton(new LndRootKeyStore(_directory));
        services.AddSingleton<LightningService>();
        services.AddSingleton<StateService>();
        services.AddSingleton<VersionerService>();
        services.AddSingleton(new HtlcInterceptorHub(NullLogger<HtlcInterceptorHub>.Instance));
        services.AddSingleton<RouterService>();
        services.AddSingleton<InvoicesService>();
        return services.BuildServiceProvider();
    }

    private IUnitOfWork CreateUnitOfWork()
    {
        var invoices = new Mock<IInvoiceDbRepository> { CallBase = true };
        invoices.Setup(x => x.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync((int skip, int take) =>
                                  _invoices.OrderByDescending(i => i.CreatedAt).Skip(skip).Take(take).ToList());
        var payments = new Mock<IPaymentDbRepository> { CallBase = true };
        payments.Setup(x => x.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync((int skip, int take) =>
                                  _payments.OrderByDescending(p => p.CreatedAt).Skip(skip).Take(take).ToList());
        var forwards = new Mock<IForwardCircuitDbRepository>();
        forwards.Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ForwardCircuitTotals(0, 0, _forwards.Count, 0, 0));
        forwards.Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ForwardCircuitListQuery query, CancellationToken _) =>
                                  _forwards.OrderByDescending(f => f.CreatedAt).Skip(query.Skip).Take(query.Take)
                                           .ToList());
        var state = new Mock<IBlockchainStateDbRepository>();
        var stored = new Mock<IChannelDbRepository>();
        stored.Setup(x => x.GetAllAsync()).ReturnsAsync(() => _closedChannels.ToList());
        var resolutions = new Mock<IOnchainResolutionDbRepository>();
        resolutions.Setup(x => x.GetClosesAsync()).ReturnsAsync(() => _closes.ToList());
        resolutions.Setup(x => x.GetOutputsByChannelIdAsync(It.IsAny<ChannelId>()))
                   .ReturnsAsync((ChannelId id) => _outputs.Where(o => o.ChannelId == id).ToList());
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(x => x.ChannelDbRepository).Returns(stored.Object);
        unitOfWork.SetupGet(x => x.OnchainResolutionDbRepository).Returns(resolutions.Object);
        unitOfWork.SetupGet(x => x.InvoiceDbRepository).Returns(invoices.Object);
        unitOfWork.SetupGet(x => x.PaymentDbRepository).Returns(payments.Object);
        unitOfWork.SetupGet(x => x.ForwardCircuitDbRepository).Returns(forwards.Object);
        unitOfWork.SetupGet(x => x.BlockchainStateDbRepository).Returns(state.Object);
        return unitOfWork.Object;
    }

    private static InvoiceModel CreateInvoice(byte tag, DateTimeOffset createdAt, InvoiceStatus status) =>
        new(new Hash(Enumerable.Repeat(tag, 32).ToArray()), new Secret(new byte[32]), new Secret(new byte[32]),
            LightningMoney.Satoshis(tag * 100), $"invoice {tag}", $"lnbcrt{tag}", createdAt, 3600, 40, status,
            status == InvoiceStatus.Settled ? LightningMoney.Satoshis(tag * 100) : null,
            status == InvoiceStatus.Settled ? createdAt.AddSeconds(30) : null)
        {
            AddIndex = tag,
            SettleIndex = status == InvoiceStatus.Settled ? tag : null
        };

    private static ChannelModel CreateChannel(byte tag, ChannelState state)
    {
        var peerId = CreatePubKey(tag);
        var txId = new TxId(Enumerable.Repeat(tag, 32).ToArray());
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1), 483, LightningMoney.Satoshis(500_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, true, FeatureSupport.No);
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null,
                                       new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), peerId, peerId, txId,
                                                             0), true, null, null,
                                       LightningMoney.Satoshis(600_000),
                                       new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId), 0, 0,
                                       LightningMoney.Satoshis(400_000), null, 0, peerId, 0, state, ChannelVersion.V1);
        if (state == ChannelState.Open)
            channel.ShortChannelId = new ShortChannelId(150, tag, 0);
        return channel;
    }

    private static CompactPubKey CreatePubKey(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }
}