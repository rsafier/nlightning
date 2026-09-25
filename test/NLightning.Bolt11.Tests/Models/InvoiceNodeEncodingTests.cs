using Moq;
using NBitcoin;

namespace NLightning.Bolt11.Tests.Models;

using Bolt11.Models;
using Bolt11.Services;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Models;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Exceptions;

/// <summary>
/// The encode path the node uses (W0-D / NL-120): sign with the node key from <see cref="ISecureKeyManager"/>,
/// the node's feature bits, <c>s</c>, <c>c</c>, <c>x</c> and several <c>r</c> fields, validated before signing.
/// </summary>
public class InvoiceNodeEncodingTests
{
    private static readonly byte[] s_nodePrivKey = Convert.FromHexString(
        "4242424242424242424242424242424242424242424242424242424242424242");

    private static readonly PubKey s_nodePubKey = new Key(s_nodePrivKey).PubKey;

    private static readonly uint256 s_paymentHash =
        new("1f3f7eaf659440ce82c47e8195c73aa128047bbfe7b0c1a135e77c52bd02030d");

    private static readonly uint256 s_paymentSecret =
        new("134ffcfacbde42da6fd75885c6fee0de12deeb9ece933e7d0edd68f458ec7cb9");

    private static readonly CompactPubKey s_bobPubKey =
        Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");

    private static readonly CompactPubKey s_carolPubKey =
        Convert.FromHexString("02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5");

    private static Mock<ISecureKeyManager> CreateKeyManager(List<byte[]> handedOutKeys)
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(x => x.GetNodeKeyPair()).Returns(() =>
        {
            // Like the real key managers, hand out a fresh copy every call
            var copy = s_nodePrivKey.ToArray();
            handedOutKeys.Add(copy);
            return new CryptoKeyPair(new PrivKey(copy), new CompactPubKey(s_nodePubKey.ToBytes()));
        });
        return keyManager;
    }

    private static Invoice CreateNodeInvoice(ISecureKeyManager? keyManager = null)
    {
        var invoice = new Invoice(LightningMoney.Satoshis(250_000), "nlightning node invoice", s_paymentHash,
                                  s_paymentSecret, BitcoinNetwork.Regtest, keyManager)
        {
            MinFinalCltvExpiry = 40
        };
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + 3_600);
        return invoice;
    }

    [Fact]
    public void Given_NodeKeyManager_When_EncodedDecodedAndValidated_Then_EveryFieldRoundTripsAndPayeeIsTheNode()
    {
        // Arrange
        var handedOutKeys = new List<byte[]>();
        var invoice = CreateNodeInvoice(CreateKeyManager(handedOutKeys).Object);

        // Act
        var encoded = invoice.Encode();
        var decoded = Invoice.Decode(encoded, BitcoinNetwork.Regtest);
        var validation = new InvoiceValidationService().ValidateForEncoding(decoded);

        // Assert
        Assert.StartsWith("lnbcrt2500u1", encoded);
        Assert.True(validation.IsValid, string.Join(", ", validation.Errors));
        Assert.Equal(s_nodePubKey, decoded.PayeePubKey);
        Assert.Equal(250_000_000UL, decoded.Amount.MilliSatoshi);
        Assert.Equal(invoice.Timestamp, decoded.Timestamp);
        Assert.Equal(s_paymentHash, decoded.PaymentHash);
        Assert.Equal(s_paymentSecret, decoded.PaymentSecret);
        Assert.Equal("nlightning node invoice", decoded.Description);
        Assert.Equal((ushort)40, decoded.MinFinalCltvExpiry);
        Assert.Equal(invoice.Timestamp + 3_600, decoded.ExpiryDate.ToUnixTimeSeconds());
        Assert.Equal(encoded, decoded.ToString());
    }

    [Fact]
    public void Given_NodeKeyManager_When_Encoded_Then_TheHandedOutPrivateKeyCopyIsZeroed()
    {
        // Arrange
        var handedOutKeys = new List<byte[]>();
        var invoice = CreateNodeInvoice(CreateKeyManager(handedOutKeys).Object);

        // Act
        _ = invoice.Encode();

        // Assert
        var copy = Assert.Single(handedOutKeys);
        Assert.All(copy, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Given_NodeInvoiceWithoutFeatures_When_Encoded_Then_OnlyVarOnionOptinAndPaymentSecretAreSetAsCompulsory()
    {
        // Arrange
        var invoice = CreateNodeInvoice();

        // Act
        var decoded = Invoice.Decode(invoice.Encode(new Key(s_nodePrivKey)), BitcoinNetwork.Regtest);

        // Assert
        Assert.NotNull(decoded.Features);
        Assert.True(decoded.Features.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.True(decoded.Features.IsFeatureSet(Feature.PaymentSecret, true));
        Assert.False(decoded.Features.HasFeature(Feature.BasicMpp));
        Assert.Equal([8, 14], decoded.Features.GetSetBits());
    }

    [Fact]
    public void Given_NodeInvoiceWithThreeRouteHints_When_EncodedAndDecoded_Then_EveryRFieldRoundTripsInOrder()
    {
        // Arrange
        var invoice = CreateNodeInvoice();
        RoutingInfoCollection first =
        [
            new(s_bobPubKey, new ShortChannelId(108, 1, 1), 1_000, 100, 40),
            new(s_carolPubKey, new ShortChannelId(109, 1, 1), 2_000, 500, 40)
        ];
        RoutingInfoCollection second = [new(s_carolPubKey, new ShortChannelId(110, 1, 1), 0, 1, 144)];
        RoutingInfoCollection third =
        [
            new(s_bobPubKey, new ShortChannelId(16_000_000, 4_000, 65_535), uint.MaxValue, uint.MaxValue,
                ushort.MaxValue)
        ];
        invoice.AddRouteHint(first);
        invoice.AddRouteHint(second);
        invoice.AddRouteHint(third);

        // Act
        var decoded = Invoice.Decode(invoice.Encode(new Key(s_nodePrivKey)), BitcoinNetwork.Regtest);

        // Assert
        Assert.Equal(3, decoded.RouteHints.Count);
        AssertSameRoute(first, decoded.RouteHints[0]);
        AssertSameRoute(second, decoded.RouteHints[1]);
        AssertSameRoute(third, decoded.RouteHints[2]);
    }

    [Fact]
    public void Given_InvoiceWithoutPaymentSecret_When_Encoded_Then_ItIsRejectedAndNothingIsSigned()
    {
        // Arrange
        var invoice = new Invoice(BitcoinNetwork.Regtest, LightningMoney.Satoshis(1))
        {
            PaymentHash = s_paymentHash,
            Description = "no secret"
        };

        // Act
        var exception = Assert.Throws<InvoiceSerializationException>(() => invoice.Encode(new Key(s_nodePrivKey)));

        // Assert
        var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("PaymentSecret is required", inner.Message);
        Assert.Throws<InvalidOperationException>(() => invoice.ToString());
    }

    [Fact]
    public void Given_InvoiceWithoutDescription_When_Encoded_Then_ItIsRejected()
    {
        // Arrange
        var invoice = new Invoice(BitcoinNetwork.Regtest, LightningMoney.Satoshis(1))
        {
            PaymentHash = s_paymentHash,
            PaymentSecret = s_paymentSecret
        };

        // Act
        var exception = Assert.Throws<InvoiceSerializationException>(() => invoice.Encode(new Key(s_nodePrivKey)));

        // Assert
        Assert.Contains("Description", exception.InnerException?.Message);
    }

    [Fact]
    public void Given_InvoiceWithAFeatureNotAllowedInInvoices_When_Encoded_Then_ItIsRejected()
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x41, 0x00]);
        features.SetFeature(Feature.OptionAnchors, false);
        var invoice = CreateNodeInvoice();
        invoice.Features = features;

        // Act
        var exception = Assert.Throws<InvoiceSerializationException>(() => invoice.Encode(new Key(s_nodePrivKey)));

        // Assert
        Assert.Contains("OptionAnchors may not be set in an invoice", exception.InnerException?.Message);
    }

    [Fact]
    public void Given_InvoiceWithAFeatureMissingItsDependency_When_Encoded_Then_ItIsRejected()
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x41, 0x00]);
        // option_zeroconf (51) needs option_scid_alias (47), which is not allowed in invoices either
        features.SetFeature(51, true);
        var invoice = CreateNodeInvoice();
        invoice.Features = features;

        // Act
        var exception = Assert.Throws<InvoiceSerializationException>(() => invoice.Encode(new Key(s_nodePrivKey)));

        // Assert
        Assert.Contains("OptionZeroconf requires OptionScidAlias", exception.InnerException?.Message);
    }

    [Fact]
    public void Given_FeaturesWithBasicMppButNoPaymentSecret_When_ValidateFeatures_Then_DependencyErrorIsReported()
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x01, 0x00]);
        features.SetFeature(17, true);
        var invoice = CreateNodeInvoice();
        invoice.Features = features;

        // Act
        var result = new InvoiceValidationService().ValidateFeatures(invoice);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("PaymentSecret must be set", result.Errors);
        Assert.Contains("BasicMpp requires PaymentSecret", result.Errors);
    }

    [Fact]
    public void Given_InvoiceWithoutFeatures_When_ValidateFeatures_Then_FeaturesAreRequired()
    {
        // Arrange
        var invoice = CreateNodeInvoice();

        // Act
        var result = new InvoiceValidationService().ValidateFeatures(invoice);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("Features is required", result.Errors);
    }

    private static void AssertSameRoute(RoutingInfoCollection expected, RoutingInfoCollection actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal((byte[])expected[i].CompactPubKey, (byte[])actual[i].CompactPubKey);
            Assert.Equal(expected[i].ShortChannelId, actual[i].ShortChannelId);
            Assert.Equal(expected[i].FeeBaseMsat, actual[i].FeeBaseMsat);
            Assert.Equal(expected[i].FeeProportionalMillionths, actual[i].FeeProportionalMillionths);
            Assert.Equal(expected[i].CltvExpiryDelta, actual[i].CltvExpiryDelta);
        }
    }
}