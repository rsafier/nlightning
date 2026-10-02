using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments;

using Application.Payments;
using Domain.Accounting.Financial.Classification;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Models;
using Domain.Payments.Models;

/// <summary>
/// NL-645: the payment events of an offer we paid carry the offer's id (the detail a classification rule on an offer
/// matches), computed as <c>OfferService</c> computes ours: the SHA-256 of the offer's TLV bytes.
/// </summary>
public class PaymentOfferIdDetailTests
{
    private static readonly byte[] s_offerBytes = [0x0a, 0x03, 0x74, 0x65, 0x61, 0x16, 0x21, 0x02, .. new byte[32]];
    private static readonly string s_offer = Bolt12Bech32.Encode(Bolt12Constants.OfferHrp, s_offerBytes);
    private static readonly string s_offerId = new Hash(SHA256.HashData(s_offerBytes)).ToString();
    private static readonly CompactPubKey s_payee = new TestNodeKeyManager(0x0d).NodeId;

    [Fact]
    public void Given_AnOfferWePaid_When_ThePaymentSucceedsOrFails_Then_BothEventsCarryTheOfferId()
    {
        // Arrange
        var succeeded = Payment();
        succeeded.Succeed(new Secret(new byte[32]), DateTimeOffset.UnixEpoch.AddDays(1));
        var failed = Payment();
        failed.Fail(null, null, "no route", DateTimeOffset.UnixEpoch.AddDays(1));

        // Act
        var paid = PaymentAccountingEvents.PaymentSucceeded(succeeded, 1, false, null);
        var refused = PaymentAccountingEvents.PaymentFailed(failed);

        // Assert
        Assert.Equal(s_offerId, paid.Details[ClassificationEngine.OfferIdDetail]);
        Assert.Equal(s_offer, paid.Details["offer"]);
        Assert.Equal(s_offerId, refused.Details[ClassificationEngine.OfferIdDetail]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lno1notbech32")]
    [InlineData("lnbcrt1somethingelse")]
    public void Given_NoUsableOffer_When_ItsIdIsAsked_Then_ThereIsNone(string? offer)
    {
        // Act
        var offerId = PaymentAccountingEvents.OfferIdOf(offer);

        // Assert
        Assert.Null(offerId);
    }

    [Fact]
    public void Given_AnOfferOfAnotherKind_When_ItsIdIsAsked_Then_ThereIsNone()
    {
        // Arrange: a BOLT 12 string that is not an offer (an invoice request's hrp)
        var request = Bolt12Bech32.Encode("lnr", s_offerBytes);

        // Act
        var offerId = PaymentAccountingEvents.OfferIdOf(request);

        // Assert
        Assert.Null(offerId);
    }

    private static PaymentModel Payment() =>
        new(new Hash(new byte[32]), null, s_payee, LightningMoney.Satoshis(1_000), LightningMoney.MilliSatoshis(50),
            DateTimeOffset.UnixEpoch, bolt12: new Bolt12PaymentDetails(s_offer, new byte[] { 1 }, new byte[32]));
}