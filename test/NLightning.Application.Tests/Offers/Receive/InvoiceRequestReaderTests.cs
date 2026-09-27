using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Offers;
using Application.Offers.Receive;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Offers.Constants;
using Domain.Offers.Enums;
using Domain.Offers.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.ValueObjects;

/// <summary>
/// B12-IRQ-02 without the offer (<see cref="InvoiceRequestReader"/>), B12-IRQ-04 against the offer
/// (<see cref="OfferInvoiceRequestRules"/>), the invoice_request rate limit and the registrations.
/// </summary>
public class InvoiceRequestReaderTests
{
    private static readonly byte[] s_offer =
        Bolt12Wire.Encode(OfferService.BuildRecords(new Domain.Offers.Models.CreateOfferRequest(null, "x"),
                                                    BitcoinNetwork.Regtest, new byte[16], [],
                                                    TestPaths.Point(0x02)));

    private static InvoiceRequestBuilder Builder() =>
        new InvoiceRequestBuilder(s_offer).Chain((byte[])ChainConstants.Regtest);

    [Fact]
    public void Given_AValidRequest_When_Read_Then_ItsFieldsAreDecoded()
    {
        // Arrange
        var bytes = Builder().Amount(5_000).Quantity(2).PayerNote("hi").Build();

        // Act
        var read = InvoiceRequestReader.TryRead(bytes, out var request, out var reason);

        // Assert
        Assert.True(read, reason);
        Assert.Equal(5_000UL, request!.Amount);
        Assert.Equal(2UL, request.Quantity);
        Assert.Equal("hi", request.PayerNote);
        Assert.Equal(ChainConstants.Regtest, request.Chain);
        Assert.Equal(s_offer, request.OfferBytes.ToArray());
        Assert.True(request.IsForOffer);
        Assert.True(request.Bip353NameIsValid);
    }

    [Theory]
    [InlineData("two signatures")]
    [InlineData("short signature")]
    [InlineData("no signature")]
    [InlineData("payer id not a point")]
    [InlineData("non-minimal amount")]
    [InlineData("chain not 32 bytes")]
    [InlineData("note not utf-8")]
    [InlineData("empty invreq_paths")]
    [InlineData("type above 2999999999")]
    public void Given_AMalformedRequest_When_Read_Then_Refused(string variant)
    {
        // Arrange
        var builder = Builder();
        var bytes = variant switch
        {
            "two signatures" => Append(builder.Build(), 242, new byte[64]),
            "short signature" => Replace(builder.Build(), Bolt12TlvTypes.Signature, new byte[63]),
            "no signature" => Bolt12Wire.Encode(builder.Records),
            "payer id not a point" => builder.Set(Bolt12TlvTypes.InvreqPayerId, new byte[33]).Build(),
            "non-minimal amount" => builder.Set(Bolt12TlvTypes.InvreqAmount, new byte[] { 0, 1 }).Build(),
            "chain not 32 bytes" => builder.Set(Bolt12TlvTypes.InvreqChain, new byte[31]).Build(),
            "note not utf-8" => builder.Set(Bolt12TlvTypes.InvreqPayerNote, new byte[] { 0xff }).Build(),
            "empty invreq_paths" => builder.Set(Bolt12TlvTypes.InvreqPaths, new byte[] { }).Build(),
            _ => builder.Set(3_000_000_001, new byte[] { 1 }).Build()
        };

        // Act
        var read = InvoiceRequestReader.TryRead(bytes, out _, out var reason);

        // Assert
        Assert.False(read);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("0461626364" + "03782d7a", true)]
    [InlineData("00" + "00", true)]
    [InlineData("0461622063" + "03782d7a", false)] // a space
    [InlineData("0461626364" + "04782d7a", false)] // domain length past the end
    [InlineData("", false)]
    public void Given_ABip353Name_When_Checked_Then_OnlyTheAllowedBytesPass(string hex, bool valid)
    {
        // Act
        var result = InvoiceRequestReader.IsValidBip353Name(Convert.FromHexString(hex));

        // Assert
        Assert.Equal(valid, result);
    }

    [Fact]
    public void Given_AnInvalidBip353Name_When_CheckedAgainstTheOffer_Then_TheFieldIsNamed()
    {
        // Arrange
        Assert.True(InvoiceRequestReader.TryRead(
                        Builder().Amount(1_000).Set(Bolt12TlvTypes.InvreqBip353Name, new byte[] { 1, 0x20, 0 })
                                 .Build(), out var request, out _));
        var offer = new OfferModel(new Hash(new byte[32]), "lno1x", s_offer, "x", null, null, null, null, null,
                                   new byte[16], OfferIssuerKind.NodeId, false, DateTimeOffset.UnixEpoch);

        // Act
        var refusal = OfferInvoiceRequestRules.Check(request!, offer, ChainConstants.Regtest, out _);

        // Assert
        Assert.Equal(Bolt12TlvTypes.InvreqBip353Name, refusal!.ErroneousField);
    }

    [Theory]
    [InlineData(null, 3UL, "invreq_quantity is not allowed")]
    [InlineData(0UL, null, "invreq_quantity is required")]
    [InlineData(0UL, 1_000_000UL, null)] // unlimited
    public void Given_QuantityRules_When_Checked_Then_TheSpecDecides(ulong? quantityMax, ulong? quantity,
                                                                    string? expectedError)
    {
        // Arrange
        var builder = Builder().Amount(1_000);
        if (quantity is { } q)
            builder.Quantity(q);
        Assert.True(InvoiceRequestReader.TryRead(builder.Build(), out var request, out _));
        var offer = new OfferModel(new Hash(new byte[32]), "lno1x", s_offer, "x", null, null, null, quantityMax, null,
                                   new byte[16], OfferIssuerKind.NodeId, false, DateTimeOffset.UnixEpoch);

        // Act
        var refusal = OfferInvoiceRequestRules.Check(request!, offer, ChainConstants.Regtest, out var amount);

        // Assert
        Assert.Equal(expectedError, refusal?.Error);
        if (expectedError is null)
            Assert.Equal(1_000UL, amount);
    }

    [Fact]
    public void Given_AnOfferAmountTimesAHugeQuantity_When_Checked_Then_Refused()
    {
        // Arrange
        Assert.True(InvoiceRequestReader.TryRead(Builder().Quantity(ulong.MaxValue).Build(), out var request, out _));
        var offer = new OfferModel(new Hash(new byte[32]), "lno1x", s_offer, "x", LightningMoney.MilliSatoshis(2),
                                   null, null, 0, null, new byte[16], OfferIssuerKind.NodeId, false,
                                   DateTimeOffset.UnixEpoch);

        // Act
        var refusal = OfferInvoiceRequestRules.Check(request!, offer, ChainConstants.Regtest, out _);

        // Assert
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Given_ALimiter_When_TheBurstIsSpent_Then_RefusedUntilRefilledAndFullBucketsAreSwept()
    {
        // Arrange
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var limiter = new InvoiceRequestRateLimiter(2, 3, clock);
        var offerA = new Hash(Enumerable.Repeat((byte)1, 32).ToArray());
        var offerB = new Hash(Enumerable.Repeat((byte)2, 32).ToArray());

        // Act / Assert: per offer 2, globally 3
        Assert.True(limiter.TryAdmit(offerA));
        Assert.True(limiter.TryAdmit(offerA));
        Assert.False(limiter.TryAdmit(offerA));
        Assert.True(limiter.TryAdmit(offerB));
        Assert.False(limiter.HasGlobalCapacity());
        Assert.False(limiter.TryAdmit(offerB));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.HasGlobalCapacity());
        Assert.True(limiter.TryAdmit(offerB));
        clock.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < InvoiceRequestRateLimiter.SweepInterval; i++)
        {
            limiter.TryAdmit(new Hash(Enumerable.Repeat((byte)3, 32).ToArray()));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, limiter.TrackedOffers);
    }

    [Fact]
    public void Given_TheRegistrations_When_Resolved_Then_TheHandlerAndServiceResolveWithoutASigner()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Domain.Protocol.Interfaces.ISecureKeyManager>(new Payments.TestNodeKeyManager(1));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton<IBlindedPaymentPathSource>(new Moq.Mock<IBlindedPaymentPathSource>().Object);
        services.AddSingleton(new Moq.Mock<Domain.Protocol.Onion.Interfaces.IRouteBlindingService>().Object);
        services.AddSingleton(new Moq.Mock<Domain.Node.Interfaces.IPeerManager>().Object);
        services.AddSingleton(new Moq.Mock<Domain.Channels.Interfaces.IChannelMemoryRepository>().Object);
        services.AddOffersServices();
        services.AddOffersServices();
        using var provider = services.BuildServiceProvider();

        // Act
        var handlers = provider.GetServices<IOnionMessageHandler>().ToList();

        // Assert
        Assert.IsType<InvoiceRequestHandler>(Assert.Single(handlers));
        var offers = provider.GetRequiredService<Domain.Offers.Interfaces.IOfferService>();
        Assert.Same(provider.GetRequiredService<OfferService>(), offers);
        Assert.False(offers.IsAvailable);
    }

    private static byte[] Append(byte[] stream, ulong type, byte[] value) =>
        [.. stream, .. Bolt12Wire.EncodeRecord(new Domain.Offers.Bolt12TlvRecord(type, value))];

    private static byte[] Replace(byte[] stream, ulong type, byte[] value)
    {
        Assert.True(Bolt12Wire.TryParse(stream, out var parsed));
        return Bolt12Wire.Encode(parsed!.Records.Select(r => r.Type == type
                                                                 ? new Domain.Offers.Bolt12TlvRecord(type, value)
                                                                 : r));
    }
}