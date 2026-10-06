using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace NLightning.LnBackend.Tests;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Google.Protobuf;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The <c>cln.Node</c> subset (the pay side and lifecycle of the Bark/ASP seam) over the node's mocked services:
/// the node identity of <c>Getinfo</c>, the request mapping of <c>Xpay</c> (amount, fee limit, retry window) and its
/// outcome-to-status mapping, and the queries and status mapping of <c>Listpays</c>. Both services live on one
/// listener in <see cref="LnBackendHostTests"/>.
/// </summary>
public sealed class ClnNodeBackendServiceTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000);

    private sealed class Harness : IDisposable
    {
        public readonly Mock<IBlockchainMonitor> BlockchainMonitor = new();
        public readonly Mock<ISecureKeyManager> KeyManager = new();
        public readonly Mock<IPaymentDbRepository> Repository = new();
        public readonly Mock<IPaymentService> PaymentService = new();
        private readonly ServiceProvider _provider;

        public byte[] NodePubKey { get; } = [0x02, .. Enumerable.Repeat((byte)0x42, 32)];

        public BitcoinNetwork Network { get; init; } = BitcoinNetwork.Regtest;

        public uint BlockHeight { get; init; } = 1_013;

        public Harness()
        {
            KeyManager.Setup(k => k.GetNodePubKey()).Returns(new CompactPubKey(NodePubKey));
            BlockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(BlockHeight);
            var collection = new ServiceCollection();
            collection.AddScoped(_ => Repository.Object);
            _provider = collection.BuildServiceProvider();
        }

        public ClnNodeBackendService Build() => new(NullLogger<ClnNodeBackendService>.Instance,
                                                    KeyManager.Object, Options.Create(
                                                        new NodeOptions { BitcoinNetwork = Network }),
                                                    BlockchainMonitor.Object, PaymentService.Object,
                                                    _provider.GetRequiredService<IServiceScopeFactory>());

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>A call context with every abstract member defaulted; the service reads only the cancellation
    /// token, never the metadata.</summary>
    private sealed class TestServerCallContext(CancellationToken cancellationToken = default) : ServerCallContext
    {
        protected override string MethodCore => "cln.Node/Method";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1:1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(10);
        protected override Metadata RequestHeadersCore => [];
        protected override Metadata ResponseTrailersCore => [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new(null, []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            null!;

        protected override Task WriteResponseHeadersAsyncCore(Metadata headers) => Task.CompletedTask;
        protected override CancellationToken CancellationTokenCore => cancellationToken;
    }

    [Fact]
    public async Task Given_TheBackend_When_Getinfo_Then_TheNodesIdentityVersionHeightAndNetworkAreServed()
    {
        // Arrange
        using var harness = new Harness();

        // Act
        var response = await harness.Build().Getinfo(new Cln.GetinfoRequest(), new TestServerCallContext());

        // Assert: the node key's compressed bytes (their client parses them as a PublicKey), the assembly version,
        // the last processed height and the network
        Assert.Equal(harness.NodePubKey, response.Id.ToByteArray());
        Assert.Matches(@"^\d+\.\d+\.\d+$", response.Version);
        Assert.Equal(1_013u, response.Blockheight);
        Assert.Equal("regtest", response.Network);
    }

    [Theory]
    [InlineData("mainnet", "bitcoin")]
    [InlineData("regtest", "regtest")]
    [InlineData("testnet", "testnet")]
    [InlineData("testnet4", "testnet4")]
    [InlineData("signet", "signet")]
    public async Task Given_EveryNetwork_When_Getinfo_Then_ClnsNetworkStringIsServed(string ours, string clns)
    {
        // Arrange
        using var harness = new Harness { Network = new BitcoinNetwork(ours) };

        // Act
        var response = await harness.Build().Getinfo(new Cln.GetinfoRequest(), new TestServerCallContext());

        // Assert: CLN's own string — mainnet is "bitcoin" there (their client's from_str takes both)
        Assert.Equal(clns, response.Network);
    }

    [Fact]
    public async Task Given_AnAmountlessInvoice_When_Xpay_Then_TheRequestsAmountBecomesThePaymentAmount()
    {
        // Arrange: an amountless invoice is the one case their caller sets amount_msat for
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync("lnbcrt1amountless", LightningMoney.MilliSatoshis(50_000),
                                             It.Is<PayInvoiceOptions>(o => o.Timeout == TimeSpan.FromSeconds(60)),
                                             It.IsAny<CancellationToken>()))
               .ReturnsAsync(Paid("lnbcrt1amountless"));

        // Act
        var response = await harness.Build().Xpay(new Cln.XpayRequest
        {
            Invstring = "lnbcrt1amountless",
            AmountMsat = new Cln.Amount { Msat = 50_000 },
            RetryFor = 60
        }, new TestServerCallContext());

        // Assert: the payment went out for the request's amount
        harness.PaymentService
               .Verify(s => s.PayInvoiceAsync("lnbcrt1amountless", LightningMoney.MilliSatoshis(50_000),
                                              It.Is<PayInvoiceOptions>(o => o.Timeout == TimeSpan.FromSeconds(60)),
                                              It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(32, response.PaymentPreimage.Length);
    }

    [Fact]
    public async Task Given_AnInvoiceWithItsOwnAmount_When_Xpay_Then_NoAmountIsPassed()
    {
        // Arrange: their caller leaves amount_msat unset when the invoice has one (CLN does not tip)
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync("lnbcrt1priced", null, It.IsAny<PayInvoiceOptions>(),
                                             It.IsAny<CancellationToken>()))
               .ReturnsAsync(Paid("lnbcrt1priced"));

        // Act
        await harness.Build().Xpay(new Cln.XpayRequest { Invstring = "lnbcrt1priced" }, new TestServerCallContext());

        // Assert
        harness.PaymentService.Verify(s => s.PayInvoiceAsync("lnbcrt1priced", null, It.IsAny<PayInvoiceOptions>(),
                                                             It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AMaxfee_When_Xpay_Then_ItBecomesTheFeeLimitAndNoneIsImposedWithoutIt()
    {
        // Arrange
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync("lnbcrt1priced", null, It.IsAny<PayInvoiceOptions>(),
                                             It.IsAny<CancellationToken>()))
               .ReturnsAsync(Paid("lnbcrt1priced"));
        var service = harness.Build();

        // Act: once with their caller's fee cap, once without one
        await service.Xpay(new Cln.XpayRequest
        {
            Invstring = "lnbcrt1priced",
            Maxfee = new Cln.Amount { Msat = 2_500 }
        }, new TestServerCallContext());
        await service.Xpay(new Cln.XpayRequest { Invstring = "lnbcrt1priced" }, new TestServerCallContext());

        // Assert
        harness.PaymentService.Verify(s => s.PayInvoiceAsync("lnbcrt1priced", null,
                                       It.Is<PayInvoiceOptions>(o => o.MaxFee == LightningMoney.MilliSatoshis(2_500)),
                                       It.IsAny<CancellationToken>()), Times.Once);
        harness.PaymentService.Verify(s => s.PayInvoiceAsync("lnbcrt1priced", null,
                                       It.Is<PayInvoiceOptions>(o => o.MaxFee == null),
                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0u, 60)]
    [InlineData(1u, 1)]
    [InlineData(45u, 45)]
    [InlineData(300u, 300)]
    [InlineData(5_000u, 300)]
    public async Task Given_ARetryWindow_When_Xpay_Then_ItIsClampedToBetween1And300Seconds(uint retryFor, int seconds)
    {
        // Arrange
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync("lnbcrt1priced", null, It.IsAny<PayInvoiceOptions>(),
                                             It.IsAny<CancellationToken>()))
               .ReturnsAsync(Paid("lnbcrt1priced"));

        // Act
        await harness.Build().Xpay(new Cln.XpayRequest { Invstring = "lnbcrt1priced", RetryFor = retryFor },
                                   new TestServerCallContext());

        // Assert: unset (0) is CLN's own 60s default; 5000s would hold the caller for far longer than any window
        // their side uses
        harness.PaymentService.Verify(s => s.PayInvoiceAsync("lnbcrt1priced", null,
                                       It.Is<PayInvoiceOptions>(o => o.Timeout == TimeSpan.FromSeconds(seconds)),
                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ASucceededPayment_When_Xpay_Then_ThePreimageAndBothAmountsAreServed()
    {
        // Arrange
        using var harness = new Harness();
        var row = Paid("lnbcrt1priced", feeMsat: 1_000, attempts: 3, parts: 2);
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync("lnbcrt1priced", null, It.IsAny<PayInvoiceOptions>(),
                                             It.IsAny<CancellationToken>()))
               .ReturnsAsync(row);

        // Act
        var response = await harness.Build().Xpay(new Cln.XpayRequest { Invstring = "lnbcrt1priced" },
                                                  new TestServerCallContext());

        // Assert: the preimage the payer needs, the payee's amount, the amount with the fee, and the parts
        Assert.Equal((byte[])row.Payment.Preimage!, response.PaymentPreimage.ToByteArray());
        Assert.Equal(50_000UL, response.AmountMsat.Msat);
        Assert.Equal(51_000UL, response.AmountSentMsat.Msat);
        Assert.Equal(2UL, response.SuccessfulParts);
        Assert.Equal(1UL, response.FailedParts);
    }

    [Fact]
    public async Task Given_AnUnparsableInvoice_When_Xpay_Then_InvalidArgument()
    {
        // Arrange
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                             It.IsAny<PayInvoiceOptions>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new ArgumentException("not a decodable invoice"));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Xpay(
                         new Cln.XpayRequest { Invstring = "lnbcrt1notaninvoice" }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Equal("not a decodable invoice", error.Status.Detail);
    }

    [Fact]
    public async Task Given_NoInvoiceAtAll_When_Xpay_Then_InvalidArgumentWithoutTouchingThePaymentService()
    {
        // Arrange
        using var harness = new Harness();

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(
                        () => harness.Build().Xpay(new Cln.XpayRequest { Invstring = "" },
                                    new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        harness.PaymentService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_APaymentAlreadyInFlightOrSucceeded_When_Xpay_Then_FailedPrecondition()
    {
        // Arrange
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                             It.IsAny<PayInvoiceOptions>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("a payment for the hash is already in flight"));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Xpay(
                         new Cln.XpayRequest { Invstring = "lnbcrt1twice" }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Fact]
    public async Task Given_APaymentStillInFlight_When_Xpay_Then_DeadlineExceeded()
    {
        // Arrange: the wait ended while the HTLC was pending — the row stays InFlight and resolves later
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                             It.IsAny<PayInvoiceOptions>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new PayInvoiceResult(Pending("lnbcrt1slow"), 1, 1));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Xpay(
                         new Cln.XpayRequest { Invstring = "lnbcrt1slow", RetryFor = 60 },
                         new TestServerCallContext()));

        // Assert: their caller keeps waiting on Listpays, where the row reports PENDING
        Assert.Equal(StatusCode.DeadlineExceeded, error.StatusCode);
        Assert.Contains("still in flight", error.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Given_TheCallerLeaves_When_Xpay_Then_CancelledWhileThePaymentKeepsGoing()
    {
        // Arrange: a payment still running when the gRPC caller goes away (captaind restarting mid-call)
        using var harness = new Harness();
        var outcome = new TaskCompletionSource<PayInvoiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken paymentToken = default;
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                             It.IsAny<PayInvoiceOptions>(), It.IsAny<CancellationToken>()))
               .Callback<string, LightningMoney?, PayInvoiceOptions, CancellationToken>((_, _, _, t) => paymentToken = t)
               .Returns(outcome.Task);
        using var caller = new CancellationTokenSource();

        // Act
        var call = harness.Build().Xpay(new Cln.XpayRequest { Invstring = "lnbcrt1leaving", RetryFor = 60 },
                                        new TestServerCallContext(caller.Token));
        await caller.CancelAsync();
        var error = await Assert.ThrowsAsync<RpcException>(() => call);

        // Assert: only the call ended — the payment never saw the caller's token (CLN's xpay keeps running in
        // lightningd), so it retries for its window and its outcome reaches Listpays
        Assert.Equal(StatusCode.Cancelled, error.StatusCode);
        Assert.False(paymentToken.CanBeCanceled);
        Assert.False(outcome.Task.IsCompleted);
        outcome.SetResult(new PayInvoiceResult(Pending("lnbcrt1leaving"), 1, 1));
    }

    [Fact]
    public async Task Given_APaymentThatXpayLeftInFlight_When_CaptaindReconcilesByHash_Then_ExactlyOnePendingRowWithItsIndex()
    {
        // Arrange: captaind's reconciliation after an xpay that ended DEADLINE_EXCEEDED — Listpays by the hash; an
        // empty answer would fail its attempt (releasing the user's HTLC VTXOs) while our HTLC is still out
        using var harness = new Harness();
        var row = Pending("lnbcrt1held");
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(row.PaymentHash)).ReturnsAsync(row);

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])row.PaymentHash)
        }, new TestServerCallContext());

        // Assert: one PENDING row (their status() maps it to Submitted, the attempt stays open), created_index set
        // (their client unwraps it with expect), no preimage and nothing reported sent
        var pay = Assert.Single(response.Pays);
        Assert.Equal(Cln.ListpaysPays.Types.ListpaysPaysStatus.Pending, pay.Status);
        Assert.True(pay.HasCreatedIndex);
        Assert.False(pay.HasPreimage);
        Assert.Equal(0ul, pay.AmountSentMsat.Msat);
    }

    [Fact]
    public async Task Given_AFailedPayment_When_Xpay_Then_FailedPreconditionWithTheReason()
    {
        // Arrange
        using var harness = new Harness();
        harness.PaymentService
               .Setup(s => s.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                             It.IsAny<PayInvoiceOptions>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new PayInvoiceResult(Failed("lnbcrt1deadend", "no route carries the amount"), 2, 1));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Xpay(
                         new Cln.XpayRequest { Invstring = "lnbcrt1deadend" }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal("no route carries the amount", error.Status.Detail);
    }

    [Fact]
    public async Task Given_APaymentByHash_When_Listpays_Then_ItIsServedWithEveryFieldMapped()
    {
        // Arrange
        using var harness = new Harness();
        var row = Paid("lnbcrt1priced", feeMsat: 1_000);
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(row.Payment.PaymentHash))
               .ReturnsAsync(row.Payment);

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])row.Payment.PaymentHash)
        }, new TestServerCallContext());

        // Assert: Succeeded is COMPLETE, with the preimage, both amounts and both timestamps; created_index is
        // always set (their client unwraps it to pick the latest attempt)
        var pays = Assert.Single(response.Pays);
        Assert.Equal((byte[])row.Payment.PaymentHash, pays.PaymentHash.ToByteArray());
        Assert.Equal(Cln.ListpaysPays.Types.ListpaysPaysStatus.Complete, pays.Status);
        Assert.Equal((byte[])row.Payment.Preimage!, pays.Preimage!.ToByteArray());
        Assert.Equal((byte[])row.Payment.PayeeNodeId, pays.Destination.ToByteArray());
        Assert.Equal("lnbcrt1priced", pays.Bolt11);
        Assert.Equal(50_000UL, pays.AmountMsat!.Msat);
        Assert.Equal(51_000UL, pays.AmountSentMsat.Msat);
        Assert.Equal((ulong)((DateTimeOffset)row.Payment.CreatedAt).ToUnixTimeSeconds(), pays.CreatedAt);
        Assert.Equal((ulong)((DateTimeOffset)row.Payment.CompletedAt!).ToUnixTimeSeconds(), pays.CompletedAt);
        Assert.True(pays.CreatedIndex > 0);
        Assert.True(pays.UpdatedIndex >= pays.CreatedIndex);
    }

    [Fact]
    public async Task Given_AnUnknownPaymentHash_When_Listpays_Then_Nothing()
    {
        // Arrange
        using var harness = new Harness();
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>()))
               .ReturnsAsync((PaymentModel?)null);

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest
        {
            PaymentHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32))
        }, new TestServerCallContext());

        // Assert
        Assert.Empty(response.Pays);
    }

    [Fact]
    public async Task Given_AHashThatIsNot32Bytes_When_Listpays_Then_InvalidArgument()
    {
        // Arrange
        using var harness = new Harness();

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Listpays(
                         new Cln.ListpaysRequest { PaymentHash = ByteString.CopyFrom([0x01, 0x02]) },
                         new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Theory]
    [InlineData(PaymentStatus.InFlight, Cln.ListpaysPays.Types.ListpaysPaysStatus.Pending)]
    [InlineData(PaymentStatus.Failed, Cln.ListpaysPays.Types.ListpaysPaysStatus.Failed)]
    [InlineData(PaymentStatus.Succeeded, Cln.ListpaysPays.Types.ListpaysPaysStatus.Complete)]
    public async Task Given_EveryPaymentStatus_When_ListedByHash_Then_ItMapsToThePaysStatus(
        PaymentStatus ours, Cln.ListpaysPays.Types.ListpaysPaysStatus theirs)
    {
        // Arrange
        using var harness = new Harness();
        var row = ours switch
        {
            PaymentStatus.Succeeded => Paid("lnbcrt1priced").Payment,
            PaymentStatus.Failed => Failed("lnbcrt1priced", "no route"),
            _ => Pending("lnbcrt1priced")
        };
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(row.PaymentHash)).ReturnsAsync(row);

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])row.PaymentHash)
        }, new TestServerCallContext());

        // Assert
        Assert.Equal(theirs, Assert.Single(response.Pays).Status);
    }

    [Fact]
    public async Task Given_PendingAndFailedRows_When_Listpays_Then_NothingWasSent()
    {
        // Arrange: only a succeeded payment sent funds — a pending or failed one sent none
        using var harness = new Harness();
        var pending = Pending("lnbcrt1pending");
        var failed = Failed("lnbcrt1failed", "no route");
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(pending.PaymentHash)).ReturnsAsync(pending);
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(failed.PaymentHash)).ReturnsAsync(failed);

        // Act
        var service = harness.Build();
        var pendingResponse = await service.Listpays(new Cln.ListpaysRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])pending.PaymentHash)
        }, new TestServerCallContext());
        var failedResponse = await service.Listpays(new Cln.ListpaysRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])failed.PaymentHash)
        }, new TestServerCallContext());

        // Assert: neither a preimage nor a sent amount before completion (an unset optional bytes reads empty)
        Assert.False(Assert.Single(pendingResponse.Pays).HasPreimage);
        Assert.Equal(0UL, Assert.Single(pendingResponse.Pays).AmountSentMsat.Msat);
        Assert.False(Assert.Single(failedResponse.Pays).HasPreimage);
        Assert.Equal(0UL, Assert.Single(failedResponse.Pays).AmountSentMsat.Msat);
    }

    [Fact]
    public async Task Given_NoConstraint_When_Listpays_Then_TheRowsArePagedNewestFirst()
    {
        // Arrange
        using var harness = new Harness();
        var rows = new[] { Pending("lnbcrt1third"), Pending("lnbcrt1second"), Pending("lnbcrt1first") };
        harness.Repository.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
               .ReturnsAsync((int skip, int take) => rows.Skip(skip).Take(take).ToList());

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest(), new TestServerCallContext());

        // Assert: the repository's page size (64, the hold side's own) is asked for, newest first
        harness.Repository.Verify(r => r.ListAsync(0, 64), Times.Once);
        Assert.Equal(rows.Select(r => (byte[])r.PaymentHash),
                     response.Pays.Select(p => p.PaymentHash.ToByteArray()).ToArray());
    }

    [Fact]
    public async Task Given_ALimit_When_Listpays_Then_NoMoreThanLimitRows()
    {
        // Arrange: more rows than the limit asks for
        using var harness = new Harness();
        var rows = new[] { Pending("lnbcrt1third"), Pending("lnbcrt1second"), Pending("lnbcrt1first") };
        harness.Repository.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
               .ReturnsAsync((int skip, int take) => rows.Skip(skip).Take(take).ToList());

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest { Limit = 2 },
                                                      new TestServerCallContext());

        // Assert
        Assert.Equal(2, response.Pays.Count);
        Assert.Equal((byte[])rows[0].PaymentHash, response.Pays[0].PaymentHash.ToByteArray());
    }

    [Fact]
    public async Task Given_AStatusFilter_When_Listpays_Then_OnlyThatStatusIsServed()
    {
        // Arrange
        using var harness = new Harness();
        var rows = new[] { Pending("lnbcrt1pending"), Failed("lnbcrt1failed", "no route"), Paid("lnbcrt1paid").Payment };
        harness.Repository.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
               .ReturnsAsync((int skip, int take) => rows.Skip(skip).Take(take).ToList());

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest
        {
            Status = Cln.ListpaysRequest.Types.ListpaysStatus.Failed
        }, new TestServerCallContext());

        // Assert: only the failed row, with the request filter's own numbering (FAILED is 2 there)
        var pays = Assert.Single(response.Pays);
        Assert.Equal((byte[])rows[1].PaymentHash, pays.PaymentHash.ToByteArray());
        Assert.Equal(Cln.ListpaysPays.Types.ListpaysPaysStatus.Failed, pays.Status);
    }

    [Fact]
    public async Task Given_ABolt11Filter_When_Listpays_Then_OnlyThatInvoiceIsServed()
    {
        // Arrange
        using var harness = new Harness();
        var rows = new[] { Pending("lnbcrt1wanted"), Pending("lnbcrt1other") };
        harness.Repository.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
               .ReturnsAsync((int skip, int take) => rows.Skip(skip).Take(take).ToList());

        // Act
        var response = await harness.Build().Listpays(new Cln.ListpaysRequest { Bolt11 = "lnbcrt1wanted" },
                                                      new TestServerCallContext());

        // Assert
        Assert.Equal((byte[])rows[0].PaymentHash, Assert.Single(response.Pays).PaymentHash.ToByteArray());
    }

    /// <summary>A succeeded payment of the standard amount, as <c>PayInvoiceAsync</c> hands it back.</summary>
    private static PayInvoiceResult Paid(string bolt11, ulong feeMsat = 0, int attempts = 1, int parts = 1)
    {
        var payment = PaymentModel.Restore(HashOf(bolt11), bolt11, Payee(), s_amount,
                                           LightningMoney.MilliSatoshis(feeMsat),
                                           DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                                           PaymentStatus.Succeeded, null, null,
                                           new Secret(Enumerable.Repeat((byte)0xcd, 32).ToArray()), null, null, null,
                                           DateTimeOffset.FromUnixTimeSeconds(1_700_000_600));
        return new PayInvoiceResult(payment, attempts, parts);
    }

    private static PaymentModel Pending(string bolt11) =>
        PaymentModel.Restore(HashOf(bolt11), bolt11, Payee(), s_amount, LightningMoney.Zero,
                             DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), PaymentStatus.InFlight, null, null,
                             null, null, null, null, null);

    private static PaymentModel Failed(string bolt11, string reason) =>
        PaymentModel.Restore(HashOf(bolt11), bolt11, Payee(), s_amount, LightningMoney.Zero,
                             DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), PaymentStatus.Failed, null, null,
                             null, null, null, reason, DateTimeOffset.FromUnixTimeSeconds(1_700_000_300));

    /// <summary>The payment hash of the row: a stable derivation from the invoice string, as a test stand-in.</summary>
    private static Hash HashOf(string bolt11) => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bolt11)));

    private static CompactPubKey Payee() => new([0x03, .. Enumerable.Repeat((byte)0x51, 32)]);
}