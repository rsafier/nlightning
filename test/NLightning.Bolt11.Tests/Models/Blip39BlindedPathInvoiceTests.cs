using NBitcoin;

namespace NLightning.Bolt11.Tests.Models;

using Bolt11.Exceptions;
using Bolt11.Models;
using Bolt11.Models.TaggedFields;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.ValueObjects;

/// <summary>
/// bLIP 39 ("BOLT 11 Invoice Blinded Path Tagged Field", merged as Draft in lightning/blips#39; the encoding LND 0.18+
/// writes): the appendix test vector byte-exact, and the reader and writer rules.
/// </summary>
public class Blip39BlindedPathInvoiceTests
{
    // bLIP 39 appendix "Test Vector": two blinded paths, one with 3 hops and one with 1 hop, no payment secret
    private const string Blip39Invoice =
        "lnbc20m1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4js5fdqqqqq2qqqqqpgqyzqqqqqqqqqqqqyqqqqqqqqqqqvsqqqqlnxy0ffrlt2y2jgtzw89kgr3zg4dlwtlfycn3yuek8x5eucnuchqps82xf0m2u6sx5wnjw7xxgnxz5kf09quqsv5zvkgj7d5kpzttp4qz7q5qsyqcyq5pzq2feycsemrh7wvendc4kw3tsmkt25a36ev6kfehrv7ecfkrp5zs9q5zqxqspqtr4avek5quzjn427asptzews5wrczfhychr2sq6ue9phmn35tjqcrspqgpsgpgxquyqjzstpsxsu59zqqqqqpqqqqqqyqq2qqqqqqqqqqqqqqqqqqqqqqqqpgqqqqk8t6endgpc99824amqzk9japgu8synwf3wx4qp4ej2r0h8rghypsqsygpf8ynzr8vwleenxdhzke69wrwed2nk8t9n2e8xudnm8pxcvxs2q5qsyqcyq5y4rdlhtf84f8rgdj34275juwls2ftxtcfh035863q3p9k6s94hpxhdmzfn5gxpsazdznxs56j4vt3fdhe00g9v2l3szher50hp4xlggqkxf77f";

    private static readonly uint256 s_hash = new(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly uint256 s_secret = new(Enumerable.Repeat((byte)2, 32).ToArray());

    private const string Blip39PrivateKey = "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734";

    [Fact]
    public void Given_Blip39TestVector_When_Decoded_Then_EveryFieldMatchesTheAppendix()
    {
        // Act
        var invoice = Invoice.Decode(Blip39Invoice, BitcoinNetwork.Mainnet);

        // Assert: "It does _not_ contain a payment secret"
        Assert.Equal(2_000_000_000UL, invoice.Amount.MilliSatoshi);
        Assert.Equal(1_496_314_658, invoice.Timestamp);
        Assert.Equal("0001020304050607080900010203040506070809000102030405060708090102",
                     invoice.PaymentHash?.ToString());
        Assert.Equal("1 cup coffee", invoice.Description);
        Assert.Null(invoice.PaymentSecret);
        Assert.Empty(invoice.RouteHints);
        Assert.Equal(new Key(Convert.FromHexString(Blip39PrivateKey)).PubKey, invoice.PayeePubKey);

        var paths = invoice.BlindedPaymentPaths;
        Assert.Equal(2, paths.Count);

        var first = paths[0];
        Assert.Equal(new BlindedPayInfo(40, 20, 130, 2, 100), first.PayInfo with { Features = default });
        Assert.Equal(0, first.PayInfo.Features.Length);
        Assert.Equal("03f3311e948feb5115242c4e396c81c448ab7ee5fd24c4e24e66c73533cc4f98b8",
                     first.Path.FirstPathKey.ToString());
        Assert.Equal(3, first.Path.Hops.Count);
        Assert.Equal("03a8c97ed5cd40d474e4ef18c899854b25e5070106504cb225e6d2c112d61a805e",
                     first.Path.FirstNodeId.ToString());
        AssertHop(first.Path.Hops[0], "03a8c97ed5cd40d474e4ef18c899854b25e5070106504cb225e6d2c112d61a805e",
                  "0102030405");
        AssertHop(first.Path.Hops[1], "0220293926219d8efe733336e2b674570dd96aa763acb3564e6e367b384d861a0a",
                  "0504030201");
        AssertHop(first.Path.Hops[2], "02c75eb336a038294eaaf760158b2e851c3c0937262e35401ae64a1bee71a2e40c",
                  "0102030405060708090a0b0c0d0e");

        var second = paths[1];
        Assert.Equal(new BlindedPayInfo(4, 2, 10, 0, 10), second.PayInfo with { Features = default });
        Assert.Equal("02c75eb336a038294eaaf760158b2e851c3c0937262e35401ae64a1bee71a2e40c",
                     second.Path.FirstPathKey.ToString());
        var hop = Assert.Single(second.Path.Hops);
        AssertHop(hop, "0220293926219d8efe733336e2b674570dd96aa763acb3564e6e367b384d861a0a", "0102030405");
        Assert.Equal(hop.BlindedNodeId, second.Path.FirstNodeId);
    }

    [Fact]
    public void Given_Blip39TestVectorFields_When_EncodedWithItsKey_Then_TheStringIsByteExact()
    {
        // Arrange: the appendix breakdown, rebuilt field by field in the vector's order (p, d, b, b)
        var decoded = Invoice.Decode(Blip39Invoice, BitcoinNetwork.Mainnet);
        var invoice = new Invoice(BitcoinNetwork.Mainnet, LightningMoney.MilliSatoshis(2_000_000_000), 1_496_314_658)
        {
            PaymentHash = decoded.PaymentHash!,
            Description = "1 cup coffee"
        };
        foreach (var path in decoded.BlindedPaymentPaths)
            invoice.AddBlindedPaymentPath(path);

        // The vector has no `9` field, which our encoder always writes (var_onion_optin and payment_secret): compare
        // the tagged field bytes of every `b` field instead of the whole string
        using var key = new Key(Convert.FromHexString(Blip39PrivateKey));

        // Act
        var reencoded = Invoice.Decode(invoice.Encode(key), BitcoinNetwork.Mainnet);

        // Assert
        Assert.Equal(decoded.BlindedPaymentPaths.Count, reencoded.BlindedPaymentPaths.Count);
        for (var i = 0; i < decoded.BlindedPaymentPaths.Count; i++)
            Assert.Equal(BlindedPaymentPathTaggedField.Serialize(decoded.BlindedPaymentPaths[i]),
                         BlindedPaymentPathTaggedField.Serialize(reencoded.BlindedPaymentPaths[i]));
        // The two `b` fields are the vector's last fields: their characters (type, data_length and data) must appear
        // unchanged in our string, which adds a `9` field after them
        var bFields = Blip39Invoice[Blip39Invoice.IndexOf("5fd", StringComparison.Ordinal)..^(104 + 6)];
        Assert.Contains(bFields, invoice.ToString(key));
        Assert.Null(reencoded.PaymentSecret);
    }

    [Fact]
    public void Given_ABlindedPath_When_SerializedAndParsed_Then_ItRoundTrips()
    {
        // Arrange
        var path = BuildPath(3, cipherLength: 300, features: [0x02, 0x00]);

        // Act
        var bytes = BlindedPaymentPathTaggedField.Serialize(path);
        var parsed = BlindedPaymentPathTaggedField.Parse(bytes);

        // Assert: a 300-byte cipher text takes a 3-byte BigSize (0xfd 0x01 0x2c)
        Assert.Equal(bytes, BlindedPaymentPathTaggedField.Serialize(parsed));
        Assert.Equal(path.PayInfo.FeeBaseMsat, parsed.PayInfo.FeeBaseMsat);
        Assert.Equal([0x02, 0x00], parsed.PayInfo.Features.ToArray());
        Assert.Equal(300, parsed.Path.Hops[0].EncryptedRecipientData.Length);
    }

    [Fact]
    public void Given_APathWithTrailingBytes_When_Parsed_Then_ItIsRejected()
    {
        // Arrange: bLIP 39 defines exactly one blinded_payinfo per `b` field
        var bytes = BlindedPaymentPathTaggedField.Serialize(BuildPath(1)).Concat(new byte[] { 0x00 }).ToArray();

        // Act / Assert
        var e = Assert.Throws<ArgumentException>(() => BlindedPaymentPathTaggedField.Parse(bytes));
        Assert.Contains("trailing", e.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    [InlineData(62)]
    public void Given_ATruncatedPath_When_Parsed_Then_ItIsRejected(int cut)
    {
        // Arrange
        var bytes = BlindedPaymentPathTaggedField.Serialize(BuildPath(2));

        // Act / Assert
        Assert.Throws<ArgumentException>(() => BlindedPaymentPathTaggedField.Parse(bytes.AsSpan(0, bytes.Length - cut)));
    }

    [Fact]
    public void Given_ZeroHops_When_Parsed_Then_ItIsRejected()
    {
        // Arrange: relay info (26) + flen 0 (2) + blinding point (33) + num_hops 0
        var bytes = BlindedPaymentPathTaggedField.Serialize(BuildPath(1))[..62];
        bytes[61] = 0;

        // Act / Assert
        var e = Assert.Throws<ArgumentException>(() => BlindedPaymentPathTaggedField.Parse(bytes));
        Assert.Contains("num_hops", e.Message);
    }

    [Fact]
    public void Given_AnInvalidBlindingPoint_When_Parsed_Then_ItIsRejected()
    {
        // Arrange: first_ephemeral_blinding_point starts at byte 28; 0x04 is not a compressed point prefix
        var bytes = BlindedPaymentPathTaggedField.Serialize(BuildPath(1));
        bytes[28] = 0x04;

        // Act / Assert
        var e = Assert.Throws<ArgumentException>(() => BlindedPaymentPathTaggedField.Parse(bytes));
        Assert.Contains("first_ephemeral_blinding_point", e.Message);
    }

    [Fact]
    public void Given_ANonCanonicalCipherLength_When_Parsed_Then_ItIsRejected()
    {
        // Arrange: the 5-byte cipher text of the only hop with its length written as 0xfd 0x00 0x05
        var bytes = BlindedPaymentPathTaggedField.Serialize(BuildPath(1, cipherLength: 5)).ToList();
        var lengthIndex = 62 + 33;
        Assert.Equal(5, bytes[lengthIndex]);
        bytes.RemoveAt(lengthIndex);
        bytes.InsertRange(lengthIndex, [0xFD, 0x00, 0x05]);

        // Act / Assert
        var e = Assert.Throws<ArgumentException>(() => BlindedPaymentPathTaggedField.Parse(bytes.ToArray()));
        Assert.Contains("canonical", e.Message);
    }

    [Fact]
    public void Given_ACipherLengthPastTheEnd_When_Parsed_Then_ItIsRejected()
    {
        // Arrange
        var bytes = BlindedPaymentPathTaggedField.Serialize(BuildPath(1, cipherLength: 5));
        bytes[62 + 33] = 6;

        // Act / Assert
        Assert.Throws<ArgumentException>(() => BlindedPaymentPathTaggedField.Parse(bytes));
    }

    [Fact]
    public void Given_AMalformedBField_When_TheTaggedFieldsAreRead_Then_TheyAreRejected()
    {
        // Arrange: a `b` field whose blinded_payinfo is followed by one more byte (a known field that is malformed
        // fails the invoice, as for `r`)
        var data = BlindedPaymentPathTaggedField.Serialize(BuildPath(1)).Concat(new byte[] { 0x00 }).ToArray();
        var groups = (short)((data.Length * 8 + 4) / 5);
        using var writer = new Domain.Utils.BitWriter(15 + groups * 5);
        writer.WriteByteAsBits(20, 5);
        writer.WriteInt16AsBits(groups, 10);
        writer.WriteBits(data, data.Length * 8);
        for (var i = data.Length * 8; i < groups * 5; i++)
            writer.WriteBit(false);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => TaggedFieldList.FromBitReader(
                                             new Domain.Utils.BitReader(writer.ToArray()), BitcoinNetwork.Regtest,
                                             15 + groups * 5));
    }

    [Fact]
    public void Given_BlindedPathsAndRouteHints_When_Encoded_Then_TheWriterRefuses()
    {
        // Arrange: bLIP 39 "An invoice containing the `b` field type: MUST not contain the `r` field type."
        using var key = new Key();
        var invoice = new Invoice(BitcoinNetwork.Regtest, LightningMoney.MilliSatoshis(1_000), 1_700_000_000)
        {
            PaymentHash = new uint256(Enumerable.Repeat((byte)1, 32).ToArray()),
            Description = "b and r"
        };
        invoice.AddBlindedPaymentPath(BuildPath(1));
        invoice.AddRouteHint([
            new Domain.Models.RoutingInfo(BuildPath(1).Path.FirstNodeId,
                                          Domain.Channels.ValueObjects.ShortChannelId.Parse("1x2x3"), 1, 1, 40)
        ]);

        // Act / Assert
        var e = Assert.Throws<InvoiceSerializationException>(() => invoice.Encode(key));
        Assert.Contains("RouteHints", e.InnerException!.Message);
    }

    [Fact]
    public void Given_BlindedPathsAndAPaymentSecret_When_Encoded_Then_TheWriterRefuses()
    {
        // Arrange: bLIP 39 "MUST not contain the `s` field type"
        using var key = new Key();
        var invoice = new Invoice(LightningMoney.MilliSatoshis(1_000), "b and s", s_hash, s_secret,
                                  BitcoinNetwork.Regtest);
        invoice.AddBlindedPaymentPath(BuildPath(1));

        // Act / Assert
        var e = Assert.Throws<InvoiceSerializationException>(() => invoice.Encode(key));
        Assert.Contains("bLIP 39", e.InnerException!.Message);

        // And without the secret it encodes, and decodes without `s`
        invoice.RemovePaymentSecret();
        var decoded = Invoice.Decode(invoice.Encode(key), BitcoinNetwork.Regtest);
        Assert.Null(decoded.PaymentSecret);
        Assert.Single(decoded.BlindedPaymentPaths);
    }

    [Fact]
    public void Given_NoBlindedPathAndNoPaymentSecret_When_Decoded_Then_TheInvoiceIsStillRejected()
    {
        // Arrange: only a `b` field lifts BOLT 11's `s` requirement
        using var key = new Key();
        var invoice = new Invoice(LightningMoney.MilliSatoshis(1_000), "no s", s_hash, s_secret,
                                  BitcoinNetwork.Regtest);
        invoice.AddBlindedPaymentPath(BuildPath(1));
        invoice.RemovePaymentSecret();
        var encoded = invoice.Encode(key);
        var withoutB = new Invoice(BitcoinNetwork.Regtest, LightningMoney.MilliSatoshis(1_000), 1_700_000_000)
        {
            PaymentHash = s_hash,
            Description = "no s"
        };

        // Act / Assert
        Assert.NotNull(Invoice.Decode(encoded, BitcoinNetwork.Regtest));
        Assert.Throws<InvoiceSerializationException>(() => withoutB.Encode(key));
    }

    [Fact]
    public void Given_TheBolt11BlindedPathsCompulsoryBit_When_Decoded_Then_ItIsKnown()
    {
        // Arrange: bLIP 39's feature (LND's Bolt11BlindedPathsRequired, bit 262) is ours to understand
        using var key = new Key();
        var invoice = new Invoice(LightningMoney.MilliSatoshis(1_000), "bit 262", s_hash, s_secret,
                                  BitcoinNetwork.Regtest);
        invoice.AddBlindedPaymentPath(BuildPath(1));
        invoice.RemovePaymentSecret();
        var features = Domain.Node.FeatureSet.DeserializeFromBytes([0x41, 0x00]);
        features.SetFeature(262, true);
        invoice.Features = features;

        // Act
        var decoded = Invoice.Decode(invoice.Encode(key), BitcoinNetwork.Regtest);

        // Assert
        Assert.True(decoded.Features!.IsFeatureSet(262, false));
    }

    private static void AssertHop(BlindedPathHop hop, string nodeId, string cipherText)
    {
        Assert.Equal(nodeId, hop.BlindedNodeId.ToString());
        Assert.Equal(cipherText, Convert.ToHexStringLower(hop.EncryptedRecipientData.Span));
    }

    internal static BlindedPaymentPath BuildPath(int hops, int cipherLength = 5, byte[]? features = null)
    {
        var hopList = new List<BlindedPathHop>();
        for (var i = 0; i < hops; i++)
        {
            using var hopKey = new Key();
            hopList.Add(new BlindedPathHop(new CompactPubKey(hopKey.PubKey.ToBytes()),
                                           Enumerable.Range(0, cipherLength).Select(b => (byte)(b + i)).ToArray()));
        }

        using var pathKey = new Key();
        var path = new BlindedPath(hopList[0].BlindedNodeId, new CompactPubKey(pathKey.PubKey.ToBytes()), hopList);
        return new BlindedPaymentPath(path, new BlindedPayInfo(1_000, 100, 144, 1, 1_000_000_000, features ?? []));
    }
}