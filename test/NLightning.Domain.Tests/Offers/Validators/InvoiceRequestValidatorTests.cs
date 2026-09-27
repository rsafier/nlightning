namespace NLightning.Domain.Tests.Offers.Validators;

using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Models;
using Domain.Offers.Validators;
using Domain.Protocol.Constants;

public class InvoiceRequestValidatorTests
{
    [Fact]
    public void Given_ValidRequest_When_Validating_Then_NoViolation()
    {
        // Arrange
        var request = InvoiceRequest.Parse(Bolt12TestData.InvoiceRequestBuilder().Build());

        // Act & Assert
        Assert.Null(InvoiceRequestValidator.Validate(request, [ChainConstants.Main]));
        Assert.Equal(Bolt12TestData.PayerId, request.Fields.PayerId);
        Assert.Equal([1, 2, 3, 4], request.Fields.Metadata!.Value.ToArray());
        Assert.Equal(Bolt12TestData.OfferBuilder().Build().Encode(), request.GetOfferStream().Encode());
    }

    public static TheoryData<string, string, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder>> InvalidRequests() =>
        new()
        {
            // B12-IRQ-02
            {
                Bolt12RequirementIds.InvoiceRequestReader, "no payer id",
                b => b.Remove(Bolt12TlvTypes.InvreqPayerId)
            },
            {
                Bolt12RequirementIds.InvoiceRequestReader, "no metadata",
                b => b.Remove(Bolt12TlvTypes.InvreqMetadata)
            },
            {
                Bolt12RequirementIds.InvoiceRequestReader, "unknown even invreq feature",
                b => b.Set(Bolt12TlvTypes.InvreqFeatures, new byte[] { 0x04 })
            },
            // B12-SIG-03
            {
                Bolt12RequirementIds.Signature, "no signature on an offer response",
                b => b.Remove(Bolt12TlvTypes.Signature)
            },
            {
                Bolt12RequirementIds.Signature, "a second signature element",
                b => b.Set(241, new byte[] { 1 })
            },
            // B12-IRQ-04, response to an offer
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "quantity missing with quantity_max",
                b => b.SetTu64(Bolt12TlvTypes.OfferQuantityMax, 5)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "quantity 0",
                b => b.SetTu64(Bolt12TlvTypes.OfferQuantityMax, 5).SetTu64(Bolt12TlvTypes.InvreqQuantity, 0)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "quantity above quantity_max",
                b => b.SetTu64(Bolt12TlvTypes.OfferQuantityMax, 5).SetTu64(Bolt12TlvTypes.InvreqQuantity, 6)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "quantity without quantity_max",
                b => b.SetTu64(Bolt12TlvTypes.InvreqQuantity, 1)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "amount below offer_amount",
                b => b.SetTu64(Bolt12TlvTypes.InvreqAmount, 9_999)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "amount below offer_amount x quantity",
                b => b.SetTu64(Bolt12TlvTypes.OfferQuantityMax, 0).SetTu64(Bolt12TlvTypes.InvreqQuantity, 3)
                      .SetTu64(Bolt12TlvTypes.InvreqAmount, 29_999)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "no amount anywhere",
                b => b.Remove(Bolt12TlvTypes.OfferAmount)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "unsupported chain",
                b => b.SetChains(Bolt12TlvTypes.InvreqChain, [ChainConstants.Testnet])
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "bip353 name with a space",
                b => b.SetBip353Name(Bolt12TlvTypes.InvreqBip353Name,
                                     new Bip353Name("bob smith"u8.ToArray(), "example.com"u8.ToArray()))
            },
            // B12-IRQ-04, not a response to an offer (refund)
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "refund with offer_chains",
                b => Refund(b).SetChains(Bolt12TlvTypes.OfferChains, [ChainConstants.Main])
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "refund with offer_features",
                b => Refund(b).Set(Bolt12TlvTypes.OfferFeatures, new byte[] { 0x02 })
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "refund with offer_quantity_max",
                b => Refund(b).SetTu64(Bolt12TlvTypes.OfferQuantityMax, 2)
            },
            {
                Bolt12RequirementIds.InvoiceRequestAmounts, "refund without invreq_amount",
                b => Refund(b).Remove(Bolt12TlvTypes.InvreqAmount)
            }
        };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Given_RequestBreakingARule_When_Validating_Then_TheRequirementIsReported(
        string requirementId, string rule, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder> change)
    {
        // Arrange
        var request = InvoiceRequest.Parse(change(Bolt12TestData.InvoiceRequestBuilder()).Build());

        // Act
        var violation = InvoiceRequestValidator.Validate(request, [ChainConstants.Main]);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal(requirementId, violation.RequirementId);
        Assert.NotNull(rule);
    }

    public static TheoryData<string, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder>> ValidRequests() => new()
    {
        { "amount equal to offer_amount", b => b.SetTu64(Bolt12TlvTypes.InvreqAmount, 10_000) },
        { "amount above offer_amount", b => b.SetTu64(Bolt12TlvTypes.InvreqAmount, 50_000) },
        {
            "quantity at quantity_max",
            b => b.SetTu64(Bolt12TlvTypes.OfferQuantityMax, 5).SetTu64(Bolt12TlvTypes.InvreqQuantity, 5)
        },
        {
            "any quantity when unlimited",
            b => b.SetTu64(Bolt12TlvTypes.OfferQuantityMax, 0).SetTu64(Bolt12TlvTypes.InvreqQuantity, 1_000)
        },
        {
            "amountless offer with invreq_amount",
            b => b.Remove(Bolt12TlvTypes.OfferAmount).SetTu64(Bolt12TlvTypes.InvreqAmount, 1)
        },
        {
            "currency offer without a converter",
            b => b.SetUtf8(Bolt12TlvTypes.OfferCurrency, "USD").SetTu64(Bolt12TlvTypes.InvreqAmount, 1)
        },
        { "unknown odd invreq feature", b => b.Set(Bolt12TlvTypes.InvreqFeatures, new byte[] { 0x08 }) },
        { "mainnet invreq_chain", b => b.SetChains(Bolt12TlvTypes.InvreqChain, [ChainConstants.Main]) },
        {
            "bip353 name",
            b => b.SetBip353Name(Bolt12TlvTypes.InvreqBip353Name,
                                 new Bip353Name("bob_smith-1.x"u8.ToArray(), "Example.COM"u8.ToArray()))
        },
        { "refund with amount, unsigned", b => Refund(b).Remove(Bolt12TlvTypes.Signature) },
        { "refund with amount, signed", Refund },
        { "payer note and paths", b => b.SetUtf8(Bolt12TlvTypes.InvreqPayerNote, "thanks")
                                        .SetPaths(Bolt12TlvTypes.InvreqPaths, [Bolt12TestData.Path()]) }
    };

    [Theory]
    [MemberData(nameof(ValidRequests))]
    public void Given_ValidRequestVariant_When_Validating_Then_NoViolation(
        string rule, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder> change)
    {
        // Arrange
        var request = InvoiceRequest.Parse(change(Bolt12TestData.InvoiceRequestBuilder()).Build());

        // Act
        var violation = InvoiceRequestValidator.Validate(request, [ChainConstants.Main]);

        // Assert
        Assert.Null(violation);
        Assert.NotNull(rule);
    }

    [Fact]
    public void Given_CurrencyOfferAndConverter_When_AmountIsBelowTheConversion_Then_ItIsRejected()
    {
        // Arrange: 10,000 cents at 1,000 msat per cent, quantity 2 = 20,000,000 msat expected
        var request = InvoiceRequest.Parse(Bolt12TestData.InvoiceRequestBuilder()
                                                         .SetUtf8(Bolt12TlvTypes.OfferCurrency, "USD")
                                                         .SetTu64(Bolt12TlvTypes.OfferQuantityMax, 0)
                                                         .SetTu64(Bolt12TlvTypes.InvreqQuantity, 2)
                                                         .SetTu64(Bolt12TlvTypes.InvreqAmount, 19_999_999)
                                                         .Build());

        // Act
        var violation = InvoiceRequestValidator.Validate(request, convertToMsat: (_, cents) => cents * 1_000);

        // Assert
        Assert.Equal(Bolt12RequirementIds.InvoiceRequestAmounts, violation!.RequirementId);
        Assert.Equal(20_000_000UL, InvoiceRequestValidator.GetExpectedAmountMsat(10_000, "USD", 2, (_, c) => c * 1_000));
        Assert.Null(InvoiceRequestValidator.GetExpectedAmountMsat(10_000, "USD", 2));
        Assert.Null(InvoiceRequestValidator.GetExpectedAmountMsat(ulong.MaxValue, null, 2));
    }

    [Theory]
    [InlineData(160UL, Bolt12RequirementIds.TlvRange)]
    [InlineData(239UL, Bolt12RequirementIds.TlvRange)]
    [InlineData(1001UL, Bolt12RequirementIds.TlvRange)]
    [InlineData(3_000_000_000UL, Bolt12RequirementIds.TlvRange)]
    [InlineData(92UL, Bolt12RequirementIds.TlvStream)]
    [InlineData(242UL, Bolt12RequirementIds.TlvStream)]
    public void Given_TypeNotAllowedInARequest_When_Parsing_Then_ItIsRejected(ulong type, string requirementId)
    {
        // Arrange
        var stream = Bolt12TestData.InvoiceRequestBuilder().Set(type, new byte[] { 1 }).Build();

        // Act
        var ok = InvoiceRequest.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(requirementId, violation!.RequirementId);
    }

    [Theory]
    [InlineData(Bolt12TlvTypes.Signature, "00")]
    [InlineData(Bolt12TlvTypes.InvreqChain, "00")]
    [InlineData(Bolt12TlvTypes.InvreqPayerId, "02")]
    [InlineData(Bolt12TlvTypes.InvreqBip353Name, "0141")]
    [InlineData(Bolt12TlvTypes.InvreqBip353Name, "01410001")]
    [InlineData(Bolt12TlvTypes.InvreqBip353Name, "0141014242")]
    [InlineData(Bolt12TlvTypes.InvreqPayerNote, "ff")]
    public void Given_MalformedRequestField_When_Parsing_Then_ItIsMalformed(ulong type, string valueHex)
    {
        // Arrange
        var stream = Bolt12TestData.InvoiceRequestBuilder().Set(type, Convert.FromHexString(valueHex)).Build();

        // Act
        var ok = InvoiceRequest.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.TlvStream, violation!.RequirementId);
        Assert.Equal(type, violation.Field);
    }

    [Fact]
    public void Given_Request_When_EncodedAsString_Then_ItParsesBack()
    {
        // Arrange
        var request = InvoiceRequest.Parse(Bolt12TestData.InvoiceRequestBuilder().Build());

        // Act
        var text = request.ToBolt12String();
        var parsed = InvoiceRequest.Parse(text);

        // Assert
        Assert.StartsWith("lnr1", text);
        Assert.True(request.Stream.ContentEquals(parsed.Stream));
        Assert.False(InvoiceRequest.TryParse(text.Replace("lnr1", "lno1"), out _, out var violation));
        Assert.Equal(Bolt12RequirementIds.Encoding, violation!.RequirementId);
        Assert.True(InvoiceRequest.TryParse(request.Stream.Encode().AsMemory(), out _, out _));
    }

    private static Bolt12TlvStreamBuilder Refund(Bolt12TlvStreamBuilder builder) =>
        builder.Remove(Bolt12TlvTypes.OfferIssuerId).Remove(Bolt12TlvTypes.OfferAmount)
               .SetTu64(Bolt12TlvTypes.InvreqAmount, 5_000);
}