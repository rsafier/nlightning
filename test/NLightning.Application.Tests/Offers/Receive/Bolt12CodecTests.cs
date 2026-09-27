using NLightning.Tests.Utils.Bolt12;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Offers.Receive;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Signing;
using Domain.Protocol.Onion.Models;

/// <summary>
/// The Domain BOLT 12 codecs the receive side calls (its <c>Bolt12Wire</c> seam was removed, NL-453): the Merkle root against
/// <c>bolt12/signature-test.json</c> (lightning/bolts master, fetched 2026-09-27), the string form, strict TLV parsing and
/// <c>blinded_payinfo</c>.
/// </summary>
public class Bolt12CodecTests
{
    // signature-test.json, the invoice_request case: issuer 0x41..., payer 0x42..., metadata 0x00 x 8
    private const string SignatureTestInvoiceRequest =
        "lnr1qqyqqqqqqqqqqqqqqcp4256ypqqkgzshgysy6ct5dpjk6ct5d93kzmpq23ex2ct5d9ek293pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpjkppqvjx204vgdzgsqpvcp4mldl3plscny0rt707gvpdh6ndydfacz43euzqhrurageg3n7kafgsek6gz3e9w52parv8gs2hlxzk95tzeswywffxlkeyhml0hh46kndmwf4m6xma3tkq2lu04qz3slje2rfthc89vss";

    private const string SignatureTestMerkle = "608407c18ad9a94d9ea2bcdbe170b6c20c462a7833a197621c916f78cf18e624";
    private const string SignatureTestSignature =
        "b8f83ea3288cfd6ea510cdb481472575141e8d8744157f98562d162cc1c472526fdb24befefbdebab4dbb726bbd1b7d8aec057f8fa805187e5950d2bbe0e5642";

    [Theory]
    // n1 test, tlv1 = 1000
    [InlineData("010203e8", "b013756c8fee86503a0b4abdab4cddeb1af5d344ca6fc2fa8b6c08938caa6f93")]
    // n1 test, tlv1 = 1000, tlv2 = 1x2x3
    [InlineData("010203e8" + "02080000010000020003",
                "c3774abbf4815aa54ccaa026bff6581f01f3be5fe814c620a252534f434bc0d1")]
    // n1 test, tlv1 = 1000, tlv2 = 1x2x3, tlv3 = 0266e4...18, 1, 2
    [InlineData("010203e8" + "02080000010000020003"
              + "03310266e4598d1d3c415f572a8488830b60f7e744ed9235eb0b1ba93283b315c0351800000000000000010000000000000002",
                "ab2e79b1283b0b31e0b035258de23782df6b89a38cfa7237bde69aed1a658c5d")]
    public void Given_SignatureTestVector_When_ComputingTheMerkleRoot_Then_ItMatches(string tlvHex, string merkleHex)
    {
        // Arrange
        Assert.True(Bolt12TlvStream.TryParse(Convert.FromHexString(tlvHex), out var stream));

        // Act
        var root = Bolt12MerkleTree.ComputeRoot(stream!);

        // Assert
        Assert.Equal(merkleHex, Convert.ToHexStringLower(root));
    }

    [Fact]
    public void Given_SignatureTestInvoiceRequest_When_ReadAndVerified_Then_RootAndSignatureMatch()
    {
        // Arrange
        var bytes = Bolt12TestStrings.Decode(SignatureTestInvoiceRequest, out var hrp);

        // Act
        var read = InvoiceRequestReader.TryRead(bytes, out var request, out var reason);
        var root = Bolt12MerkleTree.ComputeRoot(request!.Stream);

        // Assert
        Assert.Equal("lnr", hrp);
        Assert.True(read, reason);
        Assert.Equal(SignatureTestMerkle, Convert.ToHexStringLower(root));
        Assert.Equal(SignatureTestSignature, Convert.ToHexStringLower(request.Signature.Span));
        Assert.Equal(Bip340.PublicKey(Enumerable.Repeat((byte)0x42, 32).ToArray()), request.PayerId);
        Assert.True(Bip340.Verify(Bolt12Constants.InvoiceRequestSignatureTag, root, request.PayerId,
                                  request.Signature.Span));
        // offer_issuer_id is Alice's key: the request answers an offer
        Assert.True(request.IsForOffer);
        Assert.Equal(Bip340.PublicKey(Enumerable.Repeat((byte)0x41, 32).ToArray()).ToString(),
                     Convert.ToHexStringLower(request.Stream.TryGetValue(Bolt12TlvTypes.OfferIssuerId, out var issuer)
                                                  ? issuer.Span
                                                  : []));
        Assert.Equal(100UL, request.Stream.TryGetValue(Bolt12TlvTypes.OfferAmount, out var amount)
                                ? Domain.Protocol.Tlv.TruncatedInt.DecodeTu64(amount.Span)
                                : 0);
    }

    [Fact]
    public void Given_Bytes_When_EncodedAsAString_Then_TheM6EncoderAgreesAndItDecodesBack()
    {
        // Arrange
        var bytes = Enumerable.Range(0, 97).Select(i => (byte)(i * 7)).ToArray();

        // Act
        var text = Bolt12Bech32.Encode(Bolt12Constants.OfferHrp, bytes);

        // Assert
        Assert.Equal(MinimalOfferEncoder.ToBolt12String(Bolt12Constants.OfferHrp, bytes), text);
        Assert.Equal(bytes, Bolt12TestStrings.Decode(text, out var hrp));
        Assert.Equal("lno", hrp);
    }

    [Theory]
    [InlineData("0201aa0101bb", "types not increasing")]
    [InlineData("0101aa0101bb", "repeated type")]
    [InlineData("0105aa", "length past the end")]
    [InlineData("fd00fc00", "non-minimal type")]
    [InlineData("01fd000100", "non-minimal length")]
    [InlineData("01", "truncated")]
    public void Given_AnInvalidTlvStream_When_Parsed_Then_Refused(string hex, string why)
    {
        // Act
        var parsed = Bolt12TlvStream.TryParse(Convert.FromHexString(hex), out _);

        // Assert
        Assert.False(parsed, why);
    }

    [Fact]
    public void Given_Records_When_EncodedAndParsed_Then_RoundTrip()
    {
        // Arrange
        Bolt12TlvRecord[] records =
        [
            new(0, new byte[] { 1, 2 }), new(22, new byte[300]), new(1_000_000_001, ReadOnlyMemory<byte>.Empty)
        ];

        // Act
        var bytes = new Bolt12TlvStream(records).Encode();
        var parsed = Bolt12TlvStream.TryParse(bytes, out var stream);

        // Assert
        Assert.True(parsed);
        Assert.Equal(records.Select(r => r.Type), stream!.Records.Select(r => r.Type));
        Assert.Equal(records.Select(r => r.Value.Length), stream.Records.Select(r => r.Value.Length));
        Assert.Equal(bytes, stream.Encode());
    }

    [Fact]
    public void Given_APayInfo_When_Encoded_Then_TheBolt12LayoutIsWritten()
    {
        // Arrange
        var payInfo = new BlindedPayInfo(0x01020304, 0x05060708, 0x090a, 0x0b0c0d0e0f101112, 0x131415161718191a,
                                         new byte[] { 0xff });

        // Act
        var bytes = Bolt12FieldCodec.EncodePayInfos([payInfo]);

        // Assert
        Assert.Equal("01020304" + "05060708" + "090a" + "0b0c0d0e0f101112" + "131415161718191a" + "0001" + "ff",
                     Convert.ToHexStringLower(bytes));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("02", false)] // bit 1, odd
    [InlineData("01", true)] // bit 0, even
    [InlineData("020000", false)] // bit 17
    [InlineData("010000", true)] // bit 16
    public void Given_Features_When_Checked_Then_UnknownEvenBitsAreFound(string hex, bool expected)
    {
        // Act
        var found = Bolt12FieldCodec.FindUnknownEvenBit(Convert.FromHexString(hex)) is not null;

        // Assert
        Assert.Equal(expected, found);
    }
}

/// <summary>
/// A test-only BOLT 12 string decoder (bech32 5-bit words without a checksum, lower case, no <c>+</c>).
/// </summary>
internal static class Bolt12TestStrings
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    public static byte[] Decode(string text, out string hrp)
    {
        var separator = text.LastIndexOf('1');
        hrp = text[..separator];
        var bytes = new List<byte>();
        int accumulator = 0, bits = 0;
        foreach (var c in text[(separator + 1)..])
        {
            accumulator = ((accumulator << 5) | Charset.IndexOf(c)) & 0xFFF;
            bits += 5;
            if (bits < 8)
                continue;

            bits -= 8;
            bytes.Add((byte)(accumulator >> bits));
        }

        return bytes.ToArray();
    }
}