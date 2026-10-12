using NBitcoin;

namespace NLightning.Bolt11.Tests.Models;

using Bolt11.Models;
using Bolt11.Services;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Models;
using Domain.Money;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Interop fixtures against LND 0.20.0-beta (the image in <c>test/Docker/custom_lnd</c>), regtest.
/// </summary>
/// <remarks>
/// The LND strings were made by <c>lncli addinvoice</c>, REST <c>POST /v1/invoices</c> with explicit
/// <c>route_hints</c>, and <c>lncli addholdinvoice</c>; the expected values are LND's own <c>decodepayreq</c> output.
/// The NLightning string was encoded here and decoded by the same LND with <c>decodepayreq</c>.
/// </remarks>
public class LndInvoiceFixtureTests
{
    private const string LndNodeId = "0341addb0cf2665b154588117f5060ae190f81d8af1f43c541b2e8bb9e13f6f7ae";

    // lncli addinvoice --amt 50000 --memo "nlightning w0d plain" --expiry 3600 --cltv_expiry_delta 40
    private const string LndPlainInvoice =
        "lnbcrt500u1p4tdj2zpp5ps93jsl88n8l5x7qntlhmqrsvghgz9hgqad4g34yxrn6hhtadsyqdpqdekxjemgw3hxjmn8ypmnqepqwpkxz6twcqzpgxqrrsssp5uf0g8eshvnyxlxzs4zk8xlt5jn6plkq78dmdr9ak4ramzq3hal6s9qxpqysgqkac9msqmuuaye4gsmpltxaq6lfmpvwetnsargm3yhajr6h6sl32ntd6hzl0tjmdd3xfqkglkfthtzca4ym0agqgsnrklj3ld4svn55cpl5xx02";

    // POST /v1/invoices {value_msat: 250000000, expiry: 7200, cltv_expiry: 40, route_hints: [[Bob, Carol], [Carol]]}
    private const string LndRouteHintsInvoice =
        "lnbcrt2500u1p4tdj2vpp5rulhatm9j3qvaqky06qet3e65y5qg7alu7cvrgf4ua7990gzqvxsdpqdekxjemgw3hxjmn8ypmnqepqdp5kuarncqzpgxqr8pqr9yqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqqqdsqqqqgqqyqqqqlgqqqqqeqq9qpvvprlj3q76ltdxpz5qm54cp7dshrh3e9cemeu5746czdet3cfaegqqpksqqqpqqqsqqq86qqqqq05qq5qrzjqtrqglu5g8kh6mfsg4qxa9wq0nv9cauwfwxw70984wkqnw2uwz0w2qqqdcqqqqgqqyqqqqqqqqqqqqgqjqsp5zd8le7ktmepd5m7htzzudlhqmcfda6u7e6fnulgwm450gk8v0jus9qxpqysgqd9p4knp5tt57gsnwtdlfmzcp2e8pz54lsqg63ht0zvlxc48wpc6s5ysvterrsdcazxvt2fvg2yv8st57rjdujffm2g53t7w5vz8l8dqqfq0qqt";

    // lncli addholdinvoice 0101..01 --amt_msat 1234567 --memo "nlightning w0d hold" --expiry 86400 --cltv_expiry_delta 80
    private const string LndHoldInvoice =
        "lnbcrt12345670p1p4tdj2zpp5qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqsdqldekxjemgw3hxjmn8ypmnqepqdphkceqcqzzsxqyz5vqsp5qneh9p2gm6mj250tqpnjawgwqy406k903v0zq3qd28wytcqsnlzs9qxpqysgqk6ewltsaa0t75y3rup5mwqwwlsr0c8f995knnv2wmvygc3v4ud5pru9afjn575qam532vyre63ny8l67nw6z020nsf2sc5nhn3vc8ngqpm8lgy";

    // Encoded by NLightning (see the last test). LND 0.20 decodepayreq returned destination
    // 0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c, 10000000 msat, cltv_expiry 40, expiry 3600,
    // both route hints exactly as encoded, and features 8 and 14 required (nothing else)
    private const string NLightningInvoiceDecodedByLnd =
        "lnbcrt100u1p4tdj2zpp5rulhatm9j3qvaqky06qet3e65y5qg7alu7cvrgf4ua7990gzqvxssp5zd8le7ktmepd5m7htzzudlhqmcfda6u7e6fnulgwm450gk8v0jusdp9dekxjemgw3hxjmn8yphx7er9yp5kuan0d93k2cqzpgxqrrssr9yqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqqqdsqqqqgqqyqqqqlgqqqqqeqq9qpvvprlj3q76ltdxpz5qm54cp7dshrh3e9cemeu5746czdet3cfaegqqpksqqqpqqqsqqq86qqqqq05qq5qrzjqtrqglu5g8kh6mfsg4qxa9wq0nv9cauwfwxw70984wkqnw2uwz0w2qqqdcqqqqgqqyqqqqqqqqqqqqgqjq9qrsgqphgxjdk886rvaskx94tc6h3wz4242g9v2q5qzy0ehk2ny9tzh5d8nga63nzms60vqw993753fgxzl3akpdcus3jl7kgey3ecssx6wlcqva5fg5";

    private const string NLightningNodeId = "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c";

    private const string BobNodeId = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private const string CarolNodeId = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";

    [Fact]
    public void Given_LndPlainInvoice_When_Decoded_Then_FieldsMatchLndDecodePayReq()
    {
        // Act
        var invoice = Invoice.Decode(LndPlainInvoice, BitcoinNetwork.Regtest);

        // Assert
        Assert.Equal(LndNodeId, Convert.ToHexStringLower(invoice.PayeePubKey!.ToBytes()));
        Assert.Equal("0c0b1943e73ccffa1bc09aff7d8070622e8116e8075b5446a430e7abdd7d6c08", invoice.PaymentHash?.ToString());
        Assert.Equal("e25e83e61764c86f9850a8ac737d7494f41fd81e3b76d197b6a8fbb10237eff5",
                     invoice.PaymentSecret?.ToString());
        Assert.Equal(50_000_000UL, invoice.Amount.MilliSatoshi);
        Assert.Equal(1_790_363_970, invoice.Timestamp);
        Assert.Equal(1_790_363_970 + 3_600, invoice.ExpiryDate.ToUnixTimeSeconds());
        Assert.Equal("nlightning w0d plain", invoice.Description);
        Assert.Equal((ushort)40, invoice.MinFinalCltvExpiry);
        Assert.Empty(invoice.RouteHints);
        AssertLndFeatures(invoice);
    }

    [Fact]
    public void Given_LndInvoiceWithCustomRouteHints_When_Decoded_Then_BothRFieldsMatchLndDecodePayReq()
    {
        // Act
        var invoice = Invoice.Decode(LndRouteHintsInvoice, BitcoinNetwork.Regtest);

        // Assert
        Assert.Equal(LndNodeId, Convert.ToHexStringLower(invoice.PayeePubKey!.ToBytes()));
        Assert.Equal("1f3f7eaf659440ce82c47e8195c73aa128047bbfe7b0c1a135e77c52bd02030d", invoice.PaymentHash?.ToString());
        Assert.Equal("134ffcfacbde42da6fd75885c6fee0de12deeb9ece933e7d0edd68f458ec7cb9",
                     invoice.PaymentSecret?.ToString());
        Assert.Equal(250_000_000UL, invoice.Amount.MilliSatoshi);
        Assert.Equal(1_790_363_980, invoice.Timestamp);
        Assert.Equal(1_790_363_980 + 7_200, invoice.ExpiryDate.ToUnixTimeSeconds());
        Assert.Equal("nlightning w0d hints", invoice.Description);
        Assert.Equal((ushort)40, invoice.MinFinalCltvExpiry);
        AssertLndFeatures(invoice);

        Assert.Equal(2, invoice.RouteHints.Count);
        var first = invoice.RouteHints[0];
        Assert.Equal(2, first.Count);
        AssertHop(first[0], BobNodeId, 118_747_255_865_345UL, 1_000, 100, 40);
        AssertHop(first[1], CarolNodeId, 119_846_767_493_121UL, 2_000, 500, 40);
        var second = Assert.Single(invoice.RouteHints[1]);
        AssertHop(second, CarolNodeId, 120_946_279_120_897UL, 0, 1, 144);
        Assert.Equal(new ShortChannelId(108, 1, 1), first[0].ShortChannelId);
    }

    [Fact]
    public void Given_LndHoldInvoice_When_Decoded_Then_FieldsMatchLndDecodePayReq()
    {
        // Act
        var invoice = Invoice.Decode(LndHoldInvoice, BitcoinNetwork.Regtest);

        // Assert
        Assert.Equal(LndNodeId, Convert.ToHexStringLower(invoice.PayeePubKey!.ToBytes()));
        Assert.Equal("0101010101010101010101010101010101010101010101010101010101010101", invoice.PaymentHash?.ToString());
        Assert.Equal("04f3728548deb72551eb00672eb90e012afd58af8b1e20440d51dc45e0109fc5",
                     invoice.PaymentSecret?.ToString());
        // 1234567 msat is not a whole number of nano-BTC, so LND writes it with the pico multiplier
        Assert.Equal(1_234_567UL, invoice.Amount.MilliSatoshi);
        Assert.Equal(1_790_363_970, invoice.Timestamp);
        Assert.Equal(1_790_363_970 + 86_400, invoice.ExpiryDate.ToUnixTimeSeconds());
        Assert.Equal("nlightning w0d hold", invoice.Description);
        Assert.Equal((ushort)80, invoice.MinFinalCltvExpiry);
        Assert.Empty(invoice.RouteHints);
        AssertLndFeatures(invoice);
    }

    [Theory]
    [InlineData(LndPlainInvoice)]
    [InlineData(LndRouteHintsInvoice)]
    [InlineData(LndHoldInvoice)]
    public void Given_LndInvoice_When_DecodedAndValidatedWithTheEncodeRules_Then_ItPasses(string lndInvoice)
    {
        // Arrange
        var invoice = Invoice.Decode(lndInvoice, BitcoinNetwork.Regtest);

        // Act
        var result = new InvoiceValidationService().ValidateForEncoding(invoice);

        // Assert
        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Theory]
    [InlineData(LndPlainInvoice)]
    [InlineData(LndRouteHintsInvoice)]
    [InlineData(LndHoldInvoice)]
    public void Given_DecodedLndInvoice_When_ToStringCalled_Then_TheOriginalStringIsReturned(string lndInvoice)
    {
        // Arrange
        var invoice = Invoice.Decode(lndInvoice, BitcoinNetwork.Regtest);

        // Act
        var cached = invoice.ToString();

        // Assert
        Assert.Equal(lndInvoice, cached);
    }

    [Fact]
    public void Given_FixedNodeInvoice_When_Encoded_Then_ItMatchesTheStringLndDecoded()
    {
        // Arrange
        var nodeKey = new Key(Convert.FromHexString(
                                  "4242424242424242424242424242424242424242424242424242424242424242"));
        var invoice = new Invoice(BitcoinNetwork.Regtest, LightningMoney.MilliSatoshis(10_000_000), 1_790_363_970)
        {
            PaymentHash = new uint256("1f3f7eaf659440ce82c47e8195c73aa128047bbfe7b0c1a135e77c52bd02030d"),
            PaymentSecret = new uint256("134ffcfacbde42da6fd75885c6fee0de12deeb9ece933e7d0edd68f458ec7cb9"),
            Description = "nlightning node invoice",
            MinFinalCltvExpiry = 40,
            ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(1_790_363_970 + 3_600)
        };
        invoice.AddRouteHint([
            new RoutingInfo(Convert.FromHexString(BobNodeId), new ShortChannelId(108, 1, 1), 1_000, 100, 40),
            new RoutingInfo(Convert.FromHexString(CarolNodeId), new ShortChannelId(109, 1, 1), 2_000, 500, 40)
        ]);
        invoice.AddRouteHint([
            new RoutingInfo(Convert.FromHexString(CarolNodeId), new ShortChannelId(110, 1, 1), 0, 1, 144)
        ]);

        // Act
        var encoded = invoice.Encode(nodeKey);

        // Assert
        Assert.Equal(NLightningInvoiceDecodedByLnd, encoded);
        Assert.Equal(NLightningNodeId, Convert.ToHexStringLower(nodeKey.PubKey.ToBytes()));
    }

    private static void AssertLndFeatures(Invoice invoice)
    {
        // LND 0.20: tlv-onion (8) and payment-addr (14) required, multi-path-payments (17) and route-blinding (25)
        // optional
        Assert.NotNull(invoice.Features);
        Assert.True(invoice.Features.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.True(invoice.Features.IsFeatureSet(Feature.PaymentSecret, true));
        Assert.True(invoice.Features.IsFeatureSet(Feature.BasicMpp, false));
        Assert.True(invoice.Features.IsFeatureSet(Feature.OptionRouteBlinding, false));
        Assert.Equal([8, 14, 17, 25], invoice.Features.GetSetBits());
    }

    private static void AssertHop(RoutingInfo hop, string nodeId, ulong scid, uint feeBaseMsat, uint feePpm,
                                  ushort cltvExpiryDelta)
    {
        Assert.Equal(nodeId, Convert.ToHexStringLower(hop.CompactPubKey));
        Assert.Equal(new ShortChannelId(scid), hop.ShortChannelId);
        Assert.Equal(feeBaseMsat, hop.FeeBaseMsat);
        Assert.Equal(feePpm, hop.FeeProportionalMillionths);
        Assert.Equal(cltvExpiryDelta, hop.CltvExpiryDelta);
    }
}