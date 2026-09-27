namespace NLightning.Domain.Tests.Offers.Validators;

using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Validators;
using Domain.Protocol.Constants;

public class OfferValidatorTests
{
    [Fact]
    public void Given_ValidOffer_When_Validating_Then_NoViolation()
    {
        // Arrange
        var offer = Offer.Parse(Bolt12TestData.OfferBuilder().Build());

        // Act & Assert
        Assert.Null(OfferValidator.Validate(offer, DateTimeOffset.UtcNow, [ChainConstants.Main]));
    }

    public static TheoryData<string, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder>> InvalidOffers() => new()
    {
        { "unknown even feature bit 0", b => b.Set(Bolt12TlvTypes.OfferFeatures, new byte[] { 0x01 }) },
        { "unknown even feature bit 16", b => b.Set(Bolt12TlvTypes.OfferFeatures, new byte[] { 0x01, 0x00, 0x00 }) },
        { "empty offer_chains", b => b.Set(Bolt12TlvTypes.OfferChains, ReadOnlySpan<byte>.Empty) },
        { "amount without description", b => b.Remove(Bolt12TlvTypes.OfferDescription) },
        { "zero amount", b => b.SetTu64(Bolt12TlvTypes.OfferAmount, 0) },
        {
            "currency without amount",
            b => b.Remove(Bolt12TlvTypes.OfferAmount).SetUtf8(Bolt12TlvTypes.OfferCurrency, "USD")
        },
        { "no issuer and no paths", b => b.Remove(Bolt12TlvTypes.OfferIssuerId) },
        { "empty offer_paths", b => b.Set(Bolt12TlvTypes.OfferPaths, ReadOnlySpan<byte>.Empty) },
        { "expired", b => b.SetTu64(Bolt12TlvTypes.OfferAbsoluteExpiry, 1_000) },
        { "unsupported chain", b => b.SetChains(Bolt12TlvTypes.OfferChains, [ChainConstants.Testnet]) }
    };

    [Theory]
    [MemberData(nameof(InvalidOffers))]
    public void Given_OfferBreakingAReaderRule_When_Validating_Then_ItIsOfr03(
        string rule, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder> change)
    {
        // Arrange
        var offer = Offer.Parse(change(Bolt12TestData.OfferBuilder()).Build());

        // Act
        var violation = OfferValidator.Validate(offer, DateTimeOffset.UtcNow, [ChainConstants.Main]);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal(Bolt12RequirementIds.OfferReader, violation.RequirementId);
        Assert.NotNull(rule);
    }

    [Fact]
    public void Given_UnknownOddFeatureBit_When_Validating_Then_ItIsIgnored()
    {
        // Arrange
        var offer = Offer.Parse(Bolt12TestData.OfferBuilder()
                                              .Set(Bolt12TlvTypes.OfferFeatures, new byte[] { 0x02, 0x00, 0x02 })
                                              .Build());

        // Act & Assert
        Assert.Null(OfferValidator.Validate(offer));
    }

    [Fact]
    public void Given_OfferWithoutAmountOrDescription_When_Validating_Then_ItIsValid()
    {
        // Arrange: a tip offer (BOLT 12: the description is optional without an amount)
        var offer = Offer.Parse(Bolt12TestData.OfferBuilder()
                                              .Remove(Bolt12TlvTypes.OfferAmount)
                                              .Remove(Bolt12TlvTypes.OfferDescription)
                                              .Build());

        // Act & Assert
        Assert.Null(OfferValidator.Validate(offer));
    }

    [Fact]
    public void Given_PathsWithoutIssuerId_When_Validating_Then_ItIsValid()
    {
        // Arrange
        var offer = Offer.Parse(Bolt12TestData.OfferBuilder()
                                              .Remove(Bolt12TlvTypes.OfferIssuerId)
                                              .SetPaths(Bolt12TlvTypes.OfferPaths, [Bolt12TestData.Path()])
                                              .Build());

        // Act & Assert
        Assert.Null(OfferValidator.Validate(offer));
    }

    [Theory]
    [InlineData(1_999_999_999L, true)]
    [InlineData(2_000_000_000L, true)]
    [InlineData(2_000_000_001L, false)]
    public void Given_AbsoluteExpiry_When_Validating_Then_OnlyAfterItIsExpired(long nowSeconds, bool valid)
    {
        // Arrange
        var offer = Offer.Parse(Bolt12TestData.OfferBuilder()
                                              .SetTu64(Bolt12TlvTypes.OfferAbsoluteExpiry, 2_000_000_000)
                                              .Build());

        // Act
        var violation = OfferValidator.Validate(offer, DateTimeOffset.FromUnixTimeSeconds(nowSeconds));

        // Assert
        Assert.Equal(valid, violation is null);
    }

    [Fact]
    public void Given_ChainsIncludingOurs_When_Validating_Then_ItIsValid()
    {
        // Arrange
        var offer = Offer.Parse(Bolt12TestData.OfferBuilder()
                                              .SetChains(Bolt12TlvTypes.OfferChains,
                                                         [ChainConstants.Testnet, ChainConstants.Regtest])
                                              .Build());

        // Act & Assert
        Assert.Null(OfferValidator.Validate(offer, supportedChains: [ChainConstants.Regtest]));
        Assert.NotNull(OfferValidator.Validate(offer, supportedChains: [ChainConstants.Main]));
    }

    [Fact]
    public void Given_String_When_Validating_Then_TheOfferIsReturnedOnlyWhenValid()
    {
        // Arrange
        var valid = Offer.Parse(Bolt12TestData.OfferBuilder().Build()).ToBolt12String();
        var invalid = Offer.Parse(Bolt12TestData.OfferBuilder().SetTu64(Bolt12TlvTypes.OfferAmount, 0).Build())
                           .ToBolt12String();

        // Act
        var validViolation = OfferValidator.Validate(valid, out var validOffer);
        var invalidViolation = OfferValidator.Validate(invalid, out var invalidOffer);
        var brokenViolation = OfferValidator.Validate("lno1pg", out var brokenOffer);

        // Assert
        Assert.Null(validViolation);
        Assert.NotNull(validOffer);
        Assert.Equal(Bolt12RequirementIds.OfferReader, invalidViolation!.RequirementId);
        Assert.Null(invalidOffer);
        Assert.Equal(Bolt12RequirementIds.TlvStream, brokenViolation!.RequirementId);
        Assert.Null(brokenOffer);
    }
}