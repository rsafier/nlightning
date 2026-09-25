using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Bolt11.Tests.Models;

using Bolt11.Models;
using Domain.Channels.ValueObjects;
using Domain.Constants;
using Domain.Enums;
using Domain.Models;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;
using Exceptions;

public class InvoiceTests
{
    private static readonly uint256 s_testPaymentHash =
        new("0001020304050607080900010203040506070809000102030405060708090102");

    private static readonly uint256 s_testPaymentSecret =
        new("1111111111111111111111111111111111111111111111111111111111111111");

    private static readonly RoutingInfo s_defaultRoutingInfo =
        new(InitiatorValidKeysVector.RemoteStaticPublicKey, new ShortChannelId(870127, 1237, 1), 1, 1, 1);

    #region HumanReadablePart

    [Theory]
    [InlineData(NetworkConstants.Mainnet, 100_000_000_000, "lnbc1")]
    [InlineData(NetworkConstants.Testnet, 100_000_000_000, "lntb1")]
    [InlineData(NetworkConstants.Regtest, 100_000_000_000, "lnbcrt1")]
    [InlineData(NetworkConstants.Signet, 100_000_000_000, "lntbs1")]
    public void Given_NetworkType_When_InvoiceIsCreated_Then_PrefixIsCorrect(
        string network, ulong amountMsats, string expectedPrefix)
    {
        // Act
        var invoice = new Invoice(network, amountMsats);

        // Assert
        Assert.StartsWith(expectedPrefix, invoice.HumanReadablePart);
    }

    [Theory]
    [InlineData(NetworkConstants.Mainnet, 1, "lnbc10p")]
    [InlineData(NetworkConstants.Mainnet, 10, "lnbc100p")]
    [InlineData(NetworkConstants.Mainnet, 100, "lnbc1n")]
    [InlineData(NetworkConstants.Mainnet, 1_000, "lnbc10n")]
    [InlineData(NetworkConstants.Mainnet, 10_000, "lnbc100n")]
    [InlineData(NetworkConstants.Mainnet, 100_000, "lnbc1u")]
    [InlineData(NetworkConstants.Mainnet, 1_000_000, "lnbc10u")]
    [InlineData(NetworkConstants.Mainnet, 10_000_000, "lnbc100u")]
    [InlineData(NetworkConstants.Mainnet, 100_000_000, "lnbc1m")]
    [InlineData(NetworkConstants.Mainnet, 1_000_000_000, "lnbc10m")]
    [InlineData(NetworkConstants.Mainnet, 10_000_000_000, "lnbc100m")]
    [InlineData(NetworkConstants.Mainnet, 100_000_000_000, "lnbc1")]
    [InlineData(NetworkConstants.Mainnet, 1_000_000_000_000, "lnbc10")]
    [InlineData(NetworkConstants.Mainnet, 10_000_000_000_000, "lnbc100")]
    public void Given_Amount_When_InvoiceIsCreated_Then_AmountIsCorrect(string network, ulong amountMsats,
                                                                        string expectedHumanReadablePart)
    {
        // Act
        var invoice = new Invoice(network, amountMsats);

        // Assert
        Assert.Equal(expectedHumanReadablePart, invoice.HumanReadablePart);
    }

    [Fact]
    public void Given_ZeroAmount_When_InvoiceIsCreated_Then_HumanReadablePartJustContainPrefix()
    {
        // Arrange
        var network = BitcoinNetwork.Mainnet;

        // Act
        var invoice = new Invoice(network);

        // Assert
        Assert.Equal("lnbc", invoice.HumanReadablePart);
    }

    [Fact]
    public void Given_InvalidNetwork_When_InvoiceIsCreated_Then_ExceptionIsThrown()
    {
        // Arrange
        var invalidNetwork = (BitcoinNetwork)"invalid";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new Invoice(invalidNetwork, LightningMoney.Satoshis(1_000)));
    }

    [Theory]
    [InlineData(NetworkConstants.Mainnet, 1, "lnbc10n")]
    [InlineData(NetworkConstants.Mainnet, 10, "lnbc100n")]
    [InlineData(NetworkConstants.Mainnet, 100, "lnbc1u")]
    [InlineData(NetworkConstants.Mainnet, 1_000, "lnbc10u")]
    [InlineData(NetworkConstants.Mainnet, 10_000, "lnbc100u")]
    [InlineData(NetworkConstants.Mainnet, 100_000, "lnbc1m")]
    [InlineData(NetworkConstants.Mainnet, 1_000_000, "lnbc10m")]
    [InlineData(NetworkConstants.Mainnet, 10_000_000, "lnbc100m")]
    [InlineData(NetworkConstants.Mainnet, 100_000_000, "lnbc1")]
    [InlineData(NetworkConstants.Mainnet, 1_000_000_000, "lnbc10")]
    [InlineData(NetworkConstants.Mainnet, 10_000_000_000, "lnbc100")]
    public void Given_Amount_When_InvoiceIsCreatedWithInSatoshis_Then_AmountIsCorrect(
        string network, ulong amountSats, string expectedHumanReadablePart)
    {
        // Arrange
        // Act
        var invoice = Invoice.InSatoshis(amountSats, "test", s_testPaymentHash, s_testPaymentSecret, network);

        // Assert
        Assert.Equal(expectedHumanReadablePart, invoice.HumanReadablePart);
    }

    #endregion

    #region Tagged Fields

    [Fact]
    public void Given_Invoice_When_SetTaggedField_Then_InvoiceStringClearedOnChange()
    {
        // Given
        var key = new Key();
        var invoice = new Invoice(BitcoinNetwork.Mainnet);

        // "Touch" the invoice string once, so it's cached
        var initialStr = invoice.ToString(key);
        Assert.False(string.IsNullOrEmpty(initialStr));

        // When
        invoice.RoutingInfos = [s_defaultRoutingInfo];

        // Then
        // The .Changed event on routingInfoCollection should have reset the invoice's cached string to null,
        // forcing a new encoding on next invoice.ToString().
        var reencodedStr = invoice.ToString(key);
        Assert.NotEqual(initialStr, reencodedStr);
        Assert.NotNull(invoice.RoutingInfos);
        Assert.Single(invoice.RoutingInfos!);
        Assert.Equal(s_defaultRoutingInfo.CompactPubKey, invoice.RoutingInfos![0].CompactPubKey);
    }

    [Fact]
    public void Given_Invoice_When_SetFallbackAddresses_Then_TheyAreStoredAndRetrieved()
    {
        // Given
        var invoice = new Invoice(BitcoinNetwork.Mainnet);
        var addresses = new List<BitcoinAddress>
        {
            BitcoinAddress.Create("3EktnHQD7RiAE6uzMj2ZifT9YgRrkSgzQX", Network.Main),
            BitcoinAddress.Create("1RustyRX2oai4EYYDpQGWvEL62BBGqN9T", Network.Main)
        };

        // When
        invoice.FallbackAddresses = addresses;

        // Then
        Assert.NotNull(invoice.FallbackAddresses);
        Assert.Equal(2, invoice.FallbackAddresses!.Count);
        Assert.Contains(addresses[0], invoice.FallbackAddresses);
        Assert.Contains(addresses[1], invoice.FallbackAddresses);
    }

    [Fact]
    public void Given_Invoice_When_AddRoutingInfo_Then_InvoiceStringClearedOnInternalChange()
    {
        // Given
        var key = new Key();
        var invoice = new Invoice(BitcoinNetwork.Mainnet)
        {
            RoutingInfos = new RoutingInfoCollection { s_defaultRoutingInfo }
        };

        // "Touch" the invoice string once so it's cached
        var initialStr = invoice.ToString(key);
        Assert.False(string.IsNullOrEmpty(initialStr));

        // When
        invoice.RoutingInfos.Add(s_defaultRoutingInfo);

        // Then
        // The .Changed event on routingInfoCollection should have reset the invoice's cached string to null,
        // forcing a new encoding on next invoice.ToString().
        var reencodedStr = invoice.ToString(key);
        Assert.NotEqual(initialStr, reencodedStr);
        Assert.NotNull(invoice.RoutingInfos);
        Assert.Equal(2, invoice.RoutingInfos!.Count);
    }

    [Fact]
    public void Given_Invoice_When_SetExpiryDate_Then_TaggedFieldIsCreatedAndExpiryDateComputedCorrectly()
    {
        // Given
        var now = DateTimeOffset.UtcNow;
        var invoice = new Invoice(BitcoinNetwork.Mainnet, LightningMoney.Zero, now.ToUnixTimeSeconds());

        // By default, if no expiry tag is set, ExpiryDate = timestamp + DEFAULT_EXPIRATION_SECONDS
        var defaultExpiry = now.AddSeconds(InvoiceConstants.DefaultExpirationSeconds);
        Assert.Equal(defaultExpiry.ToUnixTimeSeconds(), invoice.ExpiryDate.ToUnixTimeSeconds());

        // When
        var customExpiry = now.AddHours(2);
        invoice.ExpiryDate = customExpiry; // => sets a new ExpiryTimeTaggedField

        // Then
        // The difference: customExpiry.ToUnixTimeSeconds() - now.ToUnixTimeSeconds() => stored in the field
        var newComputedExpiry = invoice.ExpiryDate;
        Assert.Equal(customExpiry.ToUnixTimeSeconds(), newComputedExpiry.ToUnixTimeSeconds());
    }

    [Fact]
    public void Given_InvoiceWithTwoRouteHints_When_EncodedAndDecoded_Then_BothRHintsAreKeptInOrder()
    {
        // Arrange
        var key = new Key();
        var invoice = new Invoice(LightningMoney.Satoshis(1_000), "two routes", s_testPaymentHash,
                                  s_testPaymentSecret, BitcoinNetwork.Mainnet)
        {
            RoutingInfos = [s_defaultRoutingInfo]
        };
        var secondHop = new RoutingInfo(InitiatorValidKeysVector.RemoteStaticPublicKey,
                                        new ShortChannelId(870128, 7, 0), 2, 3, 4);
        invoice.AddRouteHint([secondHop, s_defaultRoutingInfo]);

        // Act
        var decoded = Invoice.Decode(invoice.Encode(key), BitcoinNetwork.Mainnet);

        // Assert
        Assert.Equal(2, decoded.RouteHints.Count);
        Assert.Single(decoded.RouteHints[0]);
        Assert.Equal(s_defaultRoutingInfo.ShortChannelId, decoded.RouteHints[0][0].ShortChannelId);
        Assert.Equal(2, decoded.RouteHints[1].Count);
        Assert.Equal(secondHop.ShortChannelId, decoded.RouteHints[1][0].ShortChannelId);
        Assert.Equal(secondHop.CltvExpiryDelta, decoded.RouteHints[1][0].CltvExpiryDelta);
        Assert.NotNull(decoded.RoutingInfos);
        Assert.Equal(s_defaultRoutingInfo.ShortChannelId, decoded.RoutingInfos[0].ShortChannelId);
    }

    [Fact]
    public void Given_InvoiceWithoutRouteHints_When_RouteHintsRead_Then_IsEmpty()
    {
        // Arrange
        var invoice = new Invoice(BitcoinNetwork.Mainnet);

        // Act
        var hints = invoice.RouteHints;

        // Assert
        Assert.Empty(hints);
    }

    [Fact]
    public void Given_PropertiesSetTwice_When_Read_Then_SecondValueReplacesFirst()
    {
        // Arrange
        var invoice = new Invoice(BitcoinNetwork.Mainnet, LightningMoney.Zero, 1_496_314_658);
        var firstKey = new Key().PubKey;
        var secondKey = new Key().PubKey;
        var secondFeatures = FeatureSet.DeserializeFromBytes([0x00]);
        secondFeatures.SetFeature(Feature.BasicMpp, false);

        // Act
        invoice.PayeePubKey = firstKey;
        invoice.PayeePubKey = secondKey;
        invoice.MinFinalCltvExpiry = 40;
        invoice.MinFinalCltvExpiry = 144;
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(1_496_314_658 + 60);
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(1_496_314_658 + 120);
        invoice.Features = FeatureSet.DeserializeFromBytes([0x41, 0x00]);
        invoice.Features = secondFeatures;
        invoice.RoutingInfos = [s_defaultRoutingInfo];
        invoice.RoutingInfos = [s_defaultRoutingInfo, s_defaultRoutingInfo];
        invoice.Metadata = [1];
        invoice.Metadata = [2, 3];

        // Assert
        Assert.Equal(secondKey, invoice.PayeePubKey);
        Assert.Equal((ushort)144, invoice.MinFinalCltvExpiry);
        Assert.Equal(1_496_314_658 + 120, invoice.ExpiryDate.ToUnixTimeSeconds());
        Assert.Same(secondFeatures, invoice.Features);
        Assert.Single(invoice.RouteHints);
        Assert.Equal(2, invoice.RoutingInfos!.Count);
        Assert.Equal(new byte[] { 2, 3 }, invoice.Metadata);
    }

    [Fact]
    public void Given_InvoiceWithoutKeyManager_When_EncodeOrToStringCalledWithoutKey_Then_InvalidOperationIsThrown()
    {
        // Arrange
        var invoice = new Invoice(LightningMoney.Satoshis(1), "no key", s_testPaymentHash, s_testPaymentSecret,
                                  BitcoinNetwork.Mainnet);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => invoice.Encode());
        Assert.Throws<InvalidOperationException>(() => invoice.ToString());
    }

    [Fact]
    public void Given_DecodedInvoice_When_FeaturesMutated_Then_CachedStringIsInvalidated()
    {
        // Arrange
        const string invoiceString =
            "lnbc20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfppj3a24vwu6r8ejrss3axul8rxldph2q7z99qrsgqz6qsgww34xlatfj6e3sngrwfy3ytkt29d2qttr8qz2mnedfqysuqypgqex4haa2h8fx3wnypranf3pdwyluftwe680jjcfp438u82xqphf75ym";
        var invoice = Invoice.Decode(invoiceString, BitcoinNetwork.Mainnet);
        Assert.NotNull(invoice.Features);

        // Act
        invoice.Features.SetFeature(Feature.BasicMpp, false);
        var reencoded = invoice.ToString(new Key());

        // Assert
        Assert.NotEqual(invoiceString, reencoded);
        var decoded = Invoice.Decode(reencoded, BitcoinNetwork.Mainnet);
        Assert.True(decoded.Features!.IsFeatureSet(Feature.BasicMpp, false));
    }

    [Fact]
    public void Given_DecodedInvoiceWithoutNField_When_Reencoded_Then_RecoveredKeyIsNotAddedAsNField()
    {
        // Arrange
        // BOLT 11 example signed with priv_key e126f68f...; payee key is recovered, there is no `n` field
        const string invoiceString =
            "lnbc20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfppj3a24vwu6r8ejrss3axul8rxldph2q7z99qrsgqz6qsgww34xlatfj6e3sngrwfy3ytkt29d2qttr8qz2mnedfqysuqypgqex4haa2h8fx3wnypranf3pdwyluftwe680jjcfp438u82xqphf75ym";
        var specKey = new Key(Convert.FromHexString(
                                  "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734"));
        var invoice = Invoice.Decode(invoiceString, BitcoinNetwork.Mainnet);

        // Act
        var reencoded = invoice.Encode(specKey);

        // Assert
        Assert.Equal(specKey.PubKey, invoice.PayeePubKey);
        Assert.Equal(invoiceString, reencoded);
    }

    [Fact]
    public void Given_DecodedInvoiceWithoutNField_When_ReencodedWithAnotherKey_Then_ItStillDecodes()
    {
        // Arrange
        const string invoiceString =
            "lnbc20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfppj3a24vwu6r8ejrss3axul8rxldph2q7z99qrsgqz6qsgww34xlatfj6e3sngrwfy3ytkt29d2qttr8qz2mnedfqysuqypgqex4haa2h8fx3wnypranf3pdwyluftwe680jjcfp438u82xqphf75ym";
        var invoice = Invoice.Decode(invoiceString, BitcoinNetwork.Mainnet);
        var otherKey = new Key();

        // Act
        invoice.Features!.SetFeature(Feature.BasicMpp, false);
        var reencoded = invoice.ToString(otherKey);

        // Assert
        Assert.NotEqual(invoiceString, reencoded);
        var decoded = Invoice.Decode(reencoded, BitcoinNetwork.Mainnet);
        Assert.Equal(otherKey.PubKey, decoded.PayeePubKey);
    }

    #endregion

    #region Encoding/Decoding

    [Fact]
    public void Given_NewInvoice_When_EncodeCalled_Then_ReturnsNonEmptyString()
    {
        // Given
        var invoice = new Invoice(LightningMoney.Satoshis(1_000), "TestDesc",
                                  uint256.Parse("ed06856213fbdf7a60d0e679f0b8502125468ae268dc353475d019762aaa2c41"),
                                  uint256.Parse("e39ea727045763c32f60a262e3b2ec358b29183697edcaf4689e6b8a49df1cdf"),
                                  BitcoinNetwork.Mainnet)
        {
            PayeePubKey = new PubKey("020202020202020202020202020202020202020202020202020202020202020202")
        };

        // When
        var encoded = invoice.Encode(new Key());

        // Then
        Assert.False(string.IsNullOrWhiteSpace(encoded));
    }

    [Fact]
    public void Given_InvoiceWithoutFeatures_When_Encoded_Then_VarOnionOptinAndPaymentSecretAreRequired()
    {
        // Arrange
        var invoice = new Invoice(LightningMoney.Satoshis(1_000), "no features", s_testPaymentHash,
                                  s_testPaymentSecret, BitcoinNetwork.Mainnet);

        // Act
        var decoded = Invoice.Decode(invoice.Encode(new Key()), BitcoinNetwork.Mainnet);

        // Assert
        Assert.NotNull(decoded.Features);
        Assert.True(decoded.Features.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.True(decoded.Features.IsFeatureSet(Feature.PaymentSecret, true));
    }

    [Fact]
    public void Given_InvoiceWithOnlyBasicMpp_When_Encoded_Then_RequiredFeaturesAreAdded()
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x00]);
        features.SetFeature(Feature.BasicMpp, false);
        var invoice = new Invoice(LightningMoney.Satoshis(1_000), "mpp", s_testPaymentHash, s_testPaymentSecret,
                                  BitcoinNetwork.Mainnet)
        {
            Features = features
        };

        // Act
        var decoded = Invoice.Decode(invoice.Encode(new Key()), BitcoinNetwork.Mainnet);

        // Assert
        Assert.NotNull(decoded.Features);
        Assert.True(decoded.Features.IsFeatureSet(Feature.BasicMpp, false));
        Assert.True(decoded.Features.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.True(decoded.Features.IsFeatureSet(Feature.PaymentSecret, true));
    }

    [Fact]
    public void Given_EmptyString_When_DecodeCalled_Then_InvoiceSerializationExceptionIsThrown()
    {
        // Given
        var invalidInvoice = string.Empty;

        // When / Then
        var ex = Assert.Throws<InvoiceSerializationException>(() => Invoice.Decode(invalidInvoice));
        Assert.Contains("Error serializing invoice", ex.Message);
    }

    [Fact]
    public void Given_InvalidInvoiceString_When_DecodeCalled_Then_InvoiceSerializationExceptionIsThrown()
    {
        // Given
        var invalidInvoice = "lnxyaaaaaa";

        // When / Then
        Assert.Throws<InvoiceSerializationException>(() => Invoice.Decode(invalidInvoice));
    }

    [Fact]
    public void Given_ValidInvoiceString_When_DecodeCalled_Then_InvoiceIsReturned()
    {
        // Given
        const string invoiceString =
            "lnbc20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfppj3a24vwu6r8ejrss3axul8rxldph2q7z99qrsgqz6qsgww34xlatfj6e3sngrwfy3ytkt29d2qttr8qz2mnedfqysuqypgqex4haa2h8fx3wnypranf3pdwyluftwe680jjcfp438u82xqphf75ym";

        // When
        var invoice = Invoice.Decode(invoiceString, BitcoinNetwork.Mainnet);

        // Then
        Assert.Equal(2000000000U, invoice.Amount.MilliSatoshi);
    }

    [Fact]
    public void Given_InvoiceWithoutCField_When_Decoded_Then_MinFinalCltvExpiryDefaultsTo18()
    {
        // Arrange
        // BOLT 11 vector (P2SH fallback) with no `c` field
        const string invoiceString =
            "lnbc20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfppj3a24vwu6r8ejrss3axul8rxldph2q7z99qrsgqz6qsgww34xlatfj6e3sngrwfy3ytkt29d2qttr8qz2mnedfqysuqypgqex4haa2h8fx3wnypranf3pdwyluftwe680jjcfp438u82xqphf75ym";

        // Act
        var invoice = Invoice.Decode(invoiceString, BitcoinNetwork.Mainnet);

        // Assert
        Assert.Equal((ushort)18, invoice.MinFinalCltvExpiry);
    }

    #endregion
}