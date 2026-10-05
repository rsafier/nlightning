using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NBitcoin;

namespace NLightning.LnBackend.Tests;

using Domain.Accounting.Labels;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.ValueObjects;
using Google.Protobuf;
using Bolt11Invoice = NLightning.Bolt11.Models.Invoice;

/// <summary>
/// The unary side of <c>hold.Hold</c> (NL-1148) over the node's mocked services: the request mapping of
/// <c>Invoice</c>/<c>Inject</c>, the <c>List</c> queries, the preimage/hash mapping of <c>Settle</c>/<c>Cancel</c> and
/// the error mapping of the hold service's exceptions. The streaming side lives in
/// <see cref="LnBackendHostTests"/> (a real host).
/// </summary>
public sealed class HoldBackendServiceTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000);

    private sealed class Harness : IDisposable
    {
        public readonly Mock<IChannelMemoryRepository> ChannelMemory = new();
        public readonly Mock<IHoldInvoiceService> HoldService = new();
        public readonly Mock<IInvoiceDbRepository> Repository = new();
        public readonly Mock<IInvoiceService> InvoiceService = new();
        private readonly ServiceProvider _provider;

        public BitcoinNetwork Network { get; init; } = BitcoinNetwork.Regtest;

        public Harness()
        {
            ChannelMemory.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
            var collection = new ServiceCollection();
            collection.AddScoped(_ => Repository.Object);
            _provider = collection.BuildServiceProvider();
        }

        public HoldBackendService Build() => new(NullLogger<HoldBackendService>.Instance, InvoiceService.Object,
                                                 HoldService.Object,
                                                 _provider.GetRequiredService<IServiceScopeFactory>(),
                                                 ChannelMemory.Object,
                                                 Options.Create(new NodeOptions { BitcoinNetwork = Network }));

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>A call context with every abstract member defaulted; the service reads only the cancellation
    /// token, never the metadata.</summary>
    private sealed class TestServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "hold.Hold/Method";
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
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    }

    [Fact]
    public async Task Given_TheBackend_When_GetInfo_Then_ItsOwnVersionIsServed()
    {
        // Act
        var response = await new Harness().Build().GetInfo(new Hold.GetInfoRequest(), null!);

        // Assert: the assembly version of NLightning.LnBackend, three parts
        Assert.Matches(@"^\d+\.\d+\.\d+$", response.Version);
    }

    [Fact]
    public async Task Given_AMemoAndAnExpiry_When_Invoice_Then_TheHoldCarriesThemAndItsBolt11IsReturned()
    {
        // Arrange
        using var harness = new Harness();
        var hash = Hash(0x01);
        harness.InvoiceService
               .Setup(s => s.CreateHoldInvoiceAsync(hash, s_amount, "swap", 1_200u, It.IsAny<SourceLabels>(),
                                                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(Row(hash, InvoiceStatus.Open));

        // Act
        var response = await harness.Build().Invoice(new Hold.InvoiceRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            AmountMsat = 50_000,
            Memo = "swap",
            Expiry = 1_200
        }, new TestServerCallContext());

        // Assert: the request's hash, amount, memo and expiry became the hold, and its BOLT 11 is handed back
        Assert.Equal("lnbcrt1test", response.Bolt11);
        harness.InvoiceService.Verify(s => s.CreateHoldInvoiceAsync(hash, s_amount, "swap", 1_200,
                                      It.Is<SourceLabels>(l => l.Label == "ln-backend"), It.IsAny<CancellationToken>()),
                                      Times.Once);
    }

    /// <summary>
    /// An unset proto3 <c>optional</c> expiry reaches the service as null (its default), never 0.
    /// </summary>
    [Fact]
    public async Task Given_NoExpiry_When_Invoice_Then_NullReachesTheInvoiceService()
    {
        // Arrange
        using var harness = new Harness();
        var hash = Hash(0x02);
        harness.InvoiceService
               .Setup(s => s.CreateHoldInvoiceAsync(hash, s_amount, "swap", null, It.IsAny<SourceLabels>(),
                                                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(Row(hash, InvoiceStatus.Open));

        // Act
        await harness.Build().Invoice(new Hold.InvoiceRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            AmountMsat = 50_000,
            Memo = "swap"
        }, new TestServerCallContext());

        // Assert: 0 went in where the default (null) was meant
        harness.InvoiceService.Verify(s => s.CreateHoldInvoiceAsync(hash, s_amount, "swap", null,
                                      It.IsAny<SourceLabels>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AZeroAmount_When_Invoice_Then_InvalidArgument()
    {
        // Arrange
        using var harness = new Harness();

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Invoice(new Hold.InvoiceRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])Hash(0x03)),
            AmountMsat = 0,
            Memo = "swap"
        }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        harness.InvoiceService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ADescriptionHash_When_Invoice_Then_TheHexEncodedHashIsTheDescription()
    {
        // Arrange: the oneof's `hash` form (the description hash, not the payment hash)
        using var harness = new Harness();
        var hash = Hash(0x04);
        var descriptionHash = RandomNumberGenerator.GetBytes(32);
        var description = Convert.ToHexString(descriptionHash).ToLowerInvariant();
        harness.InvoiceService
               .Setup(s => s.CreateHoldInvoiceAsync(hash, s_amount, description, 600u, It.IsAny<SourceLabels>(),
                                                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(Row(hash, InvoiceStatus.Open));

        // Act
        await harness.Build().Invoice(new Hold.InvoiceRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            AmountMsat = 50_000,
            Expiry = 600,
            Hash = ByteString.CopyFrom(descriptionHash)
        }, new TestServerCallContext());

        // Assert
        harness.InvoiceService.Verify(s => s.CreateHoldInvoiceAsync(hash, s_amount, description, 600,
                                      It.IsAny<SourceLabels>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_NoDescriptionAtAll_When_Invoice_Then_TheDescriptionIsEmpty()
    {
        // Arrange
        using var harness = new Harness();
        var hash = Hash(0x05);
        harness.InvoiceService
               .Setup(s => s.CreateHoldInvoiceAsync(hash, s_amount, string.Empty, 600u, It.IsAny<SourceLabels>(),
                                                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(Row(hash, InvoiceStatus.Open));

        // Act
        await harness.Build().Invoice(new Hold.InvoiceRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            AmountMsat = 50_000,
            Expiry = 600
        }, new TestServerCallContext());

        // Assert
        harness.InvoiceService.Verify(s => s.CreateHoldInvoiceAsync(hash, s_amount, string.Empty, 600,
                                      It.IsAny<SourceLabels>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ARegtestBolt11_When_Injected_Then_TheHoldCarriesItsHashAmountDescriptionAndExpiry()
    {
        // Arrange: an outside invoice with an explicit expiry and a description, signed by a throwaway key
        using var harness = new Harness();
        var (bolt11, paymentHashBytes, expirySeconds) = SignedBolt11(LightningMoney.Satoshis(1_000), 1_200, "outside");
        harness.InvoiceService
               .Setup(s => s.CreateHoldInvoiceAsync(new Hash(paymentHashBytes), LightningMoney.Satoshis(1_000),
                                                    "outside", expirySeconds, SourceLabels.None,
                                                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(Row(new Hash(paymentHashBytes), InvoiceStatus.Open));

        // Act
        await harness.Build().Inject(new Hold.InjectRequest { Invoice = bolt11 }, new TestServerCallContext());

        // Assert: the hold was created for the invoice's own payment hash, amount, description and expiry
        harness.InvoiceService.Verify(s => s.CreateHoldInvoiceAsync(new Hash(paymentHashBytes),
                                      LightningMoney.Satoshis(1_000), "outside", expirySeconds,
                                      It.IsAny<SourceLabels>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ADescriptionHashBolt11_When_Injected_Then_TheHoldHasAnEmptyDescription()
    {
        // Arrange: a `h`-field invoice has no `d` field, and the hold carries the empty string
        using var harness = new Harness();
        var invoice = new Bolt11Invoice(LightningMoney.Satoshis(2_000), new uint256(RandomUtils.GetBytes(32)),
                                        new uint256(RandomUtils.GetBytes(32)),
                                        new uint256(RandomUtils.GetBytes(32)), BitcoinNetwork.Regtest);
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + 600);
        var bolt11 = invoice.Encode(new Key());
        var paymentHashBytes = Convert.FromHexString(Bolt11Invoice.Decode(bolt11, BitcoinNetwork.Regtest)
                                                                  .PaymentHash!.ToString());
        harness.InvoiceService
               .Setup(s => s.CreateHoldInvoiceAsync(new Hash(paymentHashBytes), LightningMoney.Satoshis(2_000),
                                                    string.Empty, 600u, It.IsAny<SourceLabels>(),
                                                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(Row(new Hash(paymentHashBytes), InvoiceStatus.Open));

        // Act
        await harness.Build().Inject(new Hold.InjectRequest { Invoice = bolt11 }, new TestServerCallContext());

        // Assert
        harness.InvoiceService.Verify(s => s.CreateHoldInvoiceAsync(new Hash(paymentHashBytes),
                                      LightningMoney.Satoshis(2_000), string.Empty, 600,
                                      It.IsAny<SourceLabels>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// An amountless BOLT 11 decodes with a zero (never null) amount: the injection is refused with
    /// <c>InvalidArgument</c> before anything reaches the invoice service.
    /// </summary>
    [Fact]
    public async Task Given_AnAmountlessBolt11_When_Injected_Then_ItIsRefusedWithInvalidArgument()
    {
        // Arrange: the shared amountless fixture (null amount in the invoice's fields)
        using var harness = new Harness();
        var (invoice, _, _) = SignedBolt11(LightningMoney.Zero, 600, "amountless");

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Inject(new Hold.InjectRequest
        {
            Invoice = invoice
        }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("must carry an amount", error.Status.Detail);
        harness.InvoiceService.Verify(s => s.CreateHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<LightningMoney>(),
                                      It.IsAny<string>(), It.IsAny<uint?>(), It.IsAny<SourceLabels>(),
                                      It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_NotAnInvoice_When_Injected_Then_InvalidArgument()
    {
        // Arrange
        using var harness = new Harness();

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Inject(
                         new Hold.InjectRequest { Invoice = "lnbcrt1notaninvoice" }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.StartsWith("not a decodable invoice", error.Status.Detail, StringComparison.Ordinal);
        harness.InvoiceService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AnInvoiceOfAnotherNetwork_When_Injected_Then_InvalidArgument()
    {
        // Arrange: the node runs mainnet, the invoice is regtest
        using var harness = new Harness { Network = BitcoinNetwork.Mainnet };
        var (bolt11, _, _) = SignedBolt11(LightningMoney.Satoshis(1_000), 600, "wrong network");

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Inject(
                         new Hold.InjectRequest { Invoice = bolt11 }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        harness.InvoiceService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AnInvoiceByPaymentHash_When_Listed_Then_ItIsServedWithEveryFieldMapped()
    {
        // Arrange: the by-hash lookup needs no label (a specific hash can only be one invoice)
        using var harness = new Harness();
        var preimage = new Secret(Enumerable.Repeat((byte)0xcd, 32).ToArray());
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var settledAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_600);
        var hash = Hash(0x06);
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(hash))
               .ReturnsAsync(Row(hash, InvoiceStatus.Settled, preimage: preimage, received: s_amount,
                                 settledAt: settledAt, createdAt: createdAt));

        // Act
        var response = await harness.Build().List(
                           new Hold.ListRequest { PaymentHash = ByteString.CopyFrom((byte[])hash) }, new TestServerCallContext());

        // Assert: our Settled is their PAID, with the preimage, the BOLT 11 and both timestamps
        var listed = Assert.Single(response.Invoices);
        Assert.Equal((byte[])hash, listed.PaymentHash.ToByteArray());
        Assert.Equal((byte[])preimage, listed.Preimage.ToByteArray());
        Assert.Equal("lnbcrt1test", listed.Invoice_);
        Assert.Equal(Hold.InvoiceState.Paid, listed.State);
        Assert.Equal(1_700_000_000UL, listed.CreatedAt);
        Assert.Equal(1_700_000_600UL, listed.SettledAt);
        Assert.Equal(40UL, listed.MinCltvExpiry);
        Assert.Empty(listed.Htlcs);
        Assert.Equal(0L, listed.Id);
    }

    [Theory]
    [InlineData(InvoiceStatus.Open, Hold.InvoiceState.Unpaid)]
    [InlineData(InvoiceStatus.Held, Hold.InvoiceState.Accepted)]
    [InlineData(InvoiceStatus.Settled, Hold.InvoiceState.Paid)]
    [InlineData(InvoiceStatus.Accepted, Hold.InvoiceState.Paid)]
    [InlineData(InvoiceStatus.Canceled, Hold.InvoiceState.Cancelled)]
    public async Task Given_EveryInvoiceStatus_When_ListedByHash_Then_ItMapsToTheHoldState(
        InvoiceStatus ours, Hold.InvoiceState theirs)
    {
        // Arrange
        using var harness = new Harness();
        var hash = Hash(0x07);
        Secret? preimage = null;
        if (ours is InvoiceStatus.Accepted)
            preimage = new Secret(Hash(0x08));
        var received = ours is InvoiceStatus.Open ? null : s_amount;
        DateTimeOffset? settledAt = ours == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null;
        harness.Repository.Setup(r => r.GetByPaymentHashAsync(hash))
               .ReturnsAsync(Row(hash, ours, preimage, received, settledAt));

        // Act
        var response = await harness.Build().List(
                           new Hold.ListRequest { PaymentHash = ByteString.CopyFrom((byte[])hash) }, new TestServerCallContext());

        // Assert
        Assert.Equal(theirs, Assert.Single(response.Invoices).State);
    }

    [Fact]
    public async Task Given_AnUnknownPaymentHash_When_ListedByHash_Then_Nothing()
    {
        // Act
        var response = await new Harness().Build().List(
                           new Hold.ListRequest { PaymentHash = ByteString.CopyFrom((byte[])Hash(0x09)) },
                           new TestServerCallContext());

        // Assert
        Assert.Empty(response.Invoices);
    }

    [Fact]
    public async Task Given_APageRequest_When_Listed_Then_OnlyTheBackendsOwnInvoicesOfThatPage()
    {
        // Arrange: two holds carrying the backend's own label and one of an operator's, in repository order
        using var harness = new Harness();
        var first = Row(Hash(0x0a), InvoiceStatus.Open, label: "ln-backend");
        var foreign = Row(Hash(0x0b), InvoiceStatus.Open, label: "operator");
        var second = Row(Hash(0x0c), InvoiceStatus.Open, label: "ln-backend");
        var page = new[] { first, foreign, second };
        harness.Repository.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
               .ReturnsAsync((int skip, int take) => page.Skip(skip).Take(take).ToList());

        // Act
        var response = await harness.Build().List(new Hold.ListRequest
        {
            Pagination = new Hold.ListRequest.Types.Pagination { IndexStart = 0, Limit = 3 }
        }, new TestServerCallContext());

        // Assert: the page's window went to the repository, and only the backend's own rows come back
        harness.Repository.Verify(r => r.ListAsync(0, 3), Times.Once);
        Assert.Equal(new[] { (byte[])first.PaymentHash, (byte[])second.PaymentHash },
                     response.Invoices.Select(i => i.PaymentHash.ToByteArray()).ToArray());
    }

    [Fact]
    public async Task Given_NoConstraint_When_Listed_Then_TheFirstFullPageIsAskedFor()
    {
        // Arrange: the hold plugin pages with 64
        using var harness = new Harness();
        harness.Repository.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync([]);

        // Act
        await harness.Build().List(new Hold.ListRequest(), new TestServerCallContext());

        // Assert
        harness.Repository.Verify(r => r.ListAsync(0, 64), Times.Once);
    }

    [Fact]
    public async Task Given_AnUnboundedLimit_When_Listed_Then_ItIsCappedToThePageSize()
    {
        // Arrange
        using var harness = new Harness();
        harness.Repository.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync([]);

        // Act
        await harness.Build().List(new Hold.ListRequest
        {
            Pagination = new Hold.ListRequest.Types.Pagination { IndexStart = 0, Limit = 100_000 }
        }, new TestServerCallContext());

        // Assert
        harness.Repository.Verify(r => r.ListAsync(0, 64), Times.Once);
    }

    [Fact]
    public async Task Given_APreimage_When_Settled_Then_TheHoldServiceIsAskedForItsOwnHash()
    {
        // Arrange: the payment hash is not on the request — it is SHA256 of the preimage
        using var harness = new Harness();
        var preimageBytes = RandomNumberGenerator.GetBytes(32);
        var preimage = new Secret(preimageBytes);

        // Act
        await harness.Build().Settle(new Hold.SettleRequest
        {
            PaymentPreimage = ByteString.CopyFrom(preimageBytes)
        }, new TestServerCallContext());

        // Assert
        harness.HoldService.Verify(h => h.SettleHoldInvoiceAsync(new Hash(SHA256.HashData(preimageBytes)),
                                      preimage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AnUnknownPreimage_When_Settled_Then_NotFound()
    {
        // Arrange
        using var harness = new Harness();
        harness.HoldService.Setup(h => h.SettleHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<Secret>(),
                                                                It.IsAny<CancellationToken>()))
               .ThrowsAsync(new ArgumentException("no invoice for the payment hash"));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Settle(new Hold.SettleRequest
        {
            PaymentPreimage = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32))
        }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
        Assert.Equal("no invoice for the payment hash", error.Status.Detail);
    }

    [Fact]
    public async Task Given_AnUnheldInvoice_When_Settled_Then_FailedPrecondition()
    {
        // Arrange
        using var harness = new Harness();
        harness.HoldService.Setup(h => h.SettleHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<Secret>(),
                                                                It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("the invoice is not held"));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Settle(new Hold.SettleRequest
        {
            PaymentPreimage = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32))
        }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal("the invoice is not held", error.Status.Detail);
    }

    [Fact]
    public async Task Given_APaymentHash_When_Canceled_Then_TheHoldServiceIsAskedWithIt()
    {
        // Arrange
        using var harness = new Harness();
        var hash = Hash(0x0d);

        // Act
        await harness.Build().Cancel(new Hold.CancelRequest { PaymentHash = ByteString.CopyFrom((byte[])hash) },
                                     new TestServerCallContext());

        // Assert
        harness.HoldService.Verify(h => h.CancelHoldInvoiceAsync(hash, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AnUnknownPaymentHash_When_Canceled_Then_NotFound()
    {
        // Arrange
        using var harness = new Harness();
        harness.HoldService.Setup(h => h.CancelHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new ArgumentException("no invoice for the payment hash"));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Cancel(new Hold.CancelRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])Hash(0x0e))
        }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Given_ASettledInvoice_When_Canceled_Then_FailedPrecondition()
    {
        // Arrange
        using var harness = new Harness();
        harness.HoldService.Setup(h => h.CancelHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("the invoice is not held"));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => harness.Build().Cancel(new Hold.CancelRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])Hash(0x0f))
        }, new TestServerCallContext()));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Fact]
    public async Task Given_AnyCleanRequest_When_Cleaned_Then_NothingIsCleaned()
    {
        // Arrange: the node prunes its own invoice lifecycle; Clean is the hold plugin's own janitor
        using var harness = new Harness();
        var service = harness.Build();

        // Act
        var withoutAge = await service.Clean(new Hold.CleanRequest(), new TestServerCallContext());
        var withAge = await service.Clean(new Hold.CleanRequest { Age = 600 }, new TestServerCallContext());

        // Assert
        Assert.Equal(0UL, withoutAge.Cleaned);
        Assert.Equal(0UL, withAge.Cleaned);
    }

    [Fact]
    public async Task Given_ATrackRequest_When_Tracked_Then_Unimplemented()
    {
        // Act: Track is superseded by TrackAll
        var error = await Assert.ThrowsAsync<RpcException>(
                        () => new Harness().Build().Track(new Hold.TrackRequest(), null!, null!));

        // Assert
        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
    }

    /// <summary>A regtest BOLT 11 invoice signed by a throwaway key, with its payment hash bytes and expiry.</summary>
    private static (string Bolt11, byte[] PaymentHashBytes, uint ExpirySeconds) SignedBolt11(LightningMoney amount,
                                                                                             uint expirySeconds,
                                                                                             string description)
    {
        var invoice = new Bolt11Invoice(amount, description, new uint256(RandomUtils.GetBytes(32)),
                                        new uint256(RandomUtils.GetBytes(32)), BitcoinNetwork.Regtest);
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + expirySeconds);
        var bolt11 = invoice.Encode(new Key());
        var paymentHashBytes = Convert.FromHexString(Bolt11Invoice.Decode(bolt11, BitcoinNetwork.Regtest)
                                                                  .PaymentHash!.ToString());
        return (bolt11, paymentHashBytes, expirySeconds);
    }

    private static Hash Hash(byte b) => new(Enumerable.Repeat(b, 32).ToArray());

    private static InvoiceModel Row(Hash hash, InvoiceStatus status, Secret? preimage = null,
                                    LightningMoney? received = null, DateTimeOffset? settledAt = null,
                                    DateTimeOffset? createdAt = null, string? label = null) =>
        new(hash, preimage, new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()), s_amount, "hold",
            "lnbcrt1test", createdAt ?? DateTimeOffset.UtcNow, 600, 40, status, received,
            settledAt ?? (status == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null))
        { Label = label };
}