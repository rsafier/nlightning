namespace NLightning.Application.Tests.Offers.Send;

using Application.Offers.Send;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Protocol.Constants;
using static OfferSendTestData;

/// <summary>
/// BOLT 12 "Offers" reader (B12-OFR-03) as the payer applies it before sending an invoice_request.
/// </summary>
public class OfferToPayTests
{
    private readonly TestOfferIssuer _issuer = new();

    [Fact]
    public void Given_AValidOffer_When_Parsing_Then_EveryFieldIsRead()
    {
        // Arrange
        var path = MessagePath(Key(0x05), Key(0x06));
        var text = _issuer.CreateOffer(amountMsat: 5_000, description: "coffee", chain: Chain, paths: [path],
                                       quantityMax: 3, absoluteExpiry: (ulong)Now.ToUnixTimeSeconds() + 60);

        // Act
        var offer = OfferToPay.Parse(text, Chain, Now);

        // Assert
        Assert.Equal(5_000UL, offer.Amount);
        Assert.Equal("coffee", offer.Description);
        Assert.Equal(3UL, offer.QuantityMax);
        Assert.Equal(_issuer.NodeId, offer.IssuerId);
        Assert.Equal(path.FirstPathKey, Assert.Single(offer.Paths).FirstPathKey);
        Assert.Equal(Chain, Assert.Single(offer.Chains!));
        Assert.Equal(text, offer.Text);
    }

    [Theory]
    [InlineData("out of range", "B12-OFR-03")]
    [InlineData("unknown even", "B12-ENC-03")]
    [InlineData("other chain", "B12-OFR-03")]
    [InlineData("bitcoin only", "B12-OFR-03")]
    [InlineData("even feature", "B12-OFR-03")]
    [InlineData("amount without description", "B12-OFR-03")]
    [InlineData("zero amount", "B12-OFR-03")]
    [InlineData("currency without amount", "B12-OFR-03")]
    [InlineData("no issuer and no paths", "B12-OFR-03")]
    [InlineData("expired", "B12-OFR-03")]
    [InlineData("empty chains", "B12-OFR-03")]
    [InlineData("not an offer", "B12-ENC-02")]
    public void Given_AnOfferWeMustNotRespondTo_When_Parsing_Then_ArgumentExceptionNamesTheRule(string @case,
        string requirement)
    {
        // Arrange
        var text = @case switch
        {
            "out of range" => _issuer.CreateOffer(chain: Chain, extra: [new Bolt12TlvRecord(81, new byte[] { 1 })]),
            "unknown even" => _issuer.CreateOffer(chain: Chain, extra: [new Bolt12TlvRecord(24, new byte[] { 1 })]),
            "other chain" => _issuer.CreateOffer(chain: ChainConstants.Testnet),
            "bitcoin only" => _issuer.CreateOffer(),
            "even feature" => _issuer.CreateOffer(chain: Chain,
                                                  extra: [new Bolt12TlvRecord(Bolt12TlvTypes.OfferFeatures, new byte[] { 0x01 })]),
            "amount without description" => _issuer.CreateOffer(chain: Chain, description: null),
            "zero amount" => _issuer.CreateOffer(chain: Chain, amountMsat: 0),
            "currency without amount" => _issuer.CreateOffer(chain: Chain, amountMsat: null, currency: "USD"),
            "no issuer and no paths" => _issuer.CreateOffer(chain: Chain, withIssuerId: false),
            "expired" => _issuer.CreateOffer(chain: Chain, absoluteExpiry: (ulong)Now.ToUnixTimeSeconds() - 1),
            "empty chains" => _issuer.CreateOffer(extra: [new Bolt12TlvRecord(Bolt12TlvTypes.OfferChains, Array.Empty<byte>())]),
            _ => "lnr1qqyqqqqqqqqqqqqqq"
        };

        // Act
        var exception = Assert.Throws<ArgumentException>(() => OfferToPay.Parse(text, Chain, Now));

        // Assert
        Assert.StartsWith(requirement, exception.Message);
    }

    [Fact]
    public void Given_AnUnknownOddTypeAndOddFeature_When_Parsing_Then_TheyAreKept()
    {
        // Arrange
        var text = _issuer.CreateOffer(chain: Chain, extra:
        [
            new Bolt12TlvRecord(Bolt12TlvTypes.OfferFeatures, new byte[] { 0x02 }),
            new Bolt12TlvRecord(1_000_000_001, new byte[] { 0xEE })
        ]);

        // Act
        var offer = OfferToPay.Parse(text, Chain, Now);

        // Assert
        Assert.True(offer.Stream.TryGetValue(1_000_000_001, out var kept));
        Assert.Equal(new byte[] { 0xEE }, kept.ToArray());
    }

    [Fact]
    public void Given_ABitcoinOfferWithoutChains_When_ParsingOnMainnet_Then_Accepted()
    {
        // Arrange
        var text = _issuer.CreateOffer();

        // Act
        var offer = OfferToPay.Parse(text, ChainConstants.Main, Now);

        // Assert
        Assert.Null(offer.Chains);
    }
}