namespace NLightning.Application.Tests.Offers.Send;

using Application.Offers.Send;
using Domain.Crypto.ValueObjects;
using Domain.Offers.Constants;

/// <summary>
/// The payer's BOLT 12 wire stand-in (<see cref="Bolt12Wire"/>) against the spec's <c>bolt12/format-string-test.json</c>
/// and <c>bolt12/signature-test.json</c> (lightning/bolts master, fetched 2026-09-27), until lane B12-A's codecs
/// replace it.
/// </summary>
public class Bolt12WireTests
{
    private const string Canonical =
        "lno1pqps7sjqpgtyzm3qv4uxzmtsd3jjqer9wd3hy6tsw35k7msjzfpy7nz5yqcnygrfdej82um5wf5k2uckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg";

    private const string SignatureVectorInvoiceRequest =
        "lnr1qqyqqqqqqqqqqqqqqcp4256ypqqkgzshgysy6ct5dpjk6ct5d93kzmpq23ex2ct5d9ek293pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpjkppqvjx204vgdzgsqpvcp4mldl3plscny0rt707gvpdh6ndydfacz43euzqhrurageg3n7kafgsek6gz3e9w52parv8gs2hlxzk95tzeswywffxlkeyhml0hh46kndmwf4m6xma3tkq2lu04qz3slje2rfthc89vss";

    [Theory]
    [InlineData(Canonical)]
    [InlineData("LNO1PQPS7SJQPGTYZM3QV4UXZMTSD3JJQER9WD3HY6TSW35K7MSJZFPY7NZ5YQCNYGRFDEJ82UM5WF5K2UCKYYPWA3EYT44H6TXTXQUQH7LZ5DJGE4AFGFJN7K4RGRKUAG0JSD5XVXG")]
    [InlineData("l+no1pqps7sjqpgtyzm3qv4uxzmtsd3jjqer9wd3hy6tsw35k7msjzfpy7nz5yqcnygrfdej82um5wf5k2uckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg")]
    [InlineData("lno1pqps7sjqpgt+yzm3qv4uxzmtsd3jjqer9wd3hy6tsw3+5k7msjzfpy7nz5yqcn+ygrfdej82um5wf5k2uckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd+5xvxg")]
    [InlineData("lno1pqps7sjqpgt+ yzm3qv4uxzmtsd3jjqer9wd3hy6tsw3+  5k7msjzfpy7nz5yqcn+\nygrfdej82um5wf5k2uckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd+\r\n 5xvxg")]
    [InlineData("LNO1PQPS7SJQPGT+ YZM3QV4UXZMTSD3JJQER9WD3HY6TSW3+  5K7MSJZFPY7NZ5YQCN+\nYGRFDEJ82UM5WF5K2UCKYYPWA3EYT44H6TXTXQUQH7LZ5DJGE4AFGFJN7K4RGRKUAG0JSD+\r\n 5XVXG")]
    public void Given_AValidFormatVector_When_Decoding_Then_TheBytesEqualTheCanonicalForm(string value)
    {
        // Arrange
        var expected = Bolt12Wire.DecodeString(Canonical, Bolt12Constants.OfferHrp);

        // Act
        var decoded = Bolt12Wire.DecodeString(value, Bolt12Constants.OfferHrp);

        // Assert
        Assert.Equal(expected, decoded);
        Assert.Equal(Canonical, Bolt12Wire.EncodeString(Bolt12Constants.OfferHrp, decoded));
    }

    [Theory]
    [InlineData("LnO1PqPs7sJqPgTyZm3qV4UxZmTsD3JjQeR9Wd3hY6TsW35k7mSjZfPy7nZ5YqCnYgRfDeJ82uM5Wf5k2uCkYyPwA3EyT44h6tXtXqUqH7Lz5dJgE4AfGfJn7k4rGrKuAg0jSd5xVxG")]
    [InlineData(Canonical + "+")]
    [InlineData(Canonical + "+ ")]
    [InlineData("+" + Canonical)]
    [InlineData("+ " + Canonical)]
    [InlineData("ln++o1pqps7sjqpgtyzm3qv4uxzmtsd3jjqer9wd3hy6tsw35k7msjzfpy7nz5yqcnygrfdej82um5wf5k2uckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg")]
    [InlineData("lnr1pqps7sjqpgt")]
    public void Given_AnInvalidString_When_Decoding_Then_FormatException(string value)
    {
        // Act / Assert
        Assert.Throws<FormatException>(() => Bolt12Wire.DecodeString(value, Bolt12Constants.OfferHrp));
    }

    [Theory]
    [InlineData("010203e8", "b013756c8fee86503a0b4abdab4cddeb1af5d344ca6fc2fa8b6c08938caa6f93")]
    [InlineData("010203e802080000010000020003", "c3774abbf4815aa54ccaa026bff6581f01f3be5fe814c620a252534f434bc0d1")]
    [InlineData("010203e8020800000100000200030331"
              + "0266e4598d1d3c415f572a8488830b60f7e744ed9235eb0b1ba93283b315c03518"
              + "00000000000000010000000000000002",
                "ab2e79b1283b0b31e0b035258de23782df6b89a38cfa7237bde69aed1a658c5d")]
    public void Given_ASignatureVectorStream_When_ComputingTheMerkleRoot_Then_ItEqualsTheVector(string tlv,
        string merkle)
    {
        // Arrange
        var stream = Bolt12Wire.ParseStream(Convert.FromHexString(tlv));

        // Act
        var root = Bolt12Wire.MerkleRoot(stream);

        // Assert
        Assert.Equal(merkle, Convert.ToHexString((byte[])root).ToLowerInvariant());
        Assert.Equal(Convert.FromHexString(tlv), Bolt12Wire.Encode(stream));
    }

    [Fact]
    public void Given_TheSignatureVectorInvoiceRequest_When_Verifying_Then_RootTaggedHashAndSignatureMatch()
    {
        // Arrange
        var bytes = Bolt12Wire.DecodeString(SignatureVectorInvoiceRequest, Bolt12Constants.InvoiceRequestHrp);
        var stream = Bolt12Wire.ParseStream(bytes);
        Assert.True(stream.TryGetValue(Bolt12TlvTypes.InvreqPayerId, out var payerId));
        Assert.True(stream.TryGetValue(Bolt12TlvTypes.Signature, out var signature));

        // Act
        var root = Bolt12Wire.MerkleRoot(stream);
        var tagged = Bolt12Wire.TaggedHash(Bolt12Constants.InvoiceRequestSignatureTag, root);
        var verified = new TestBolt12Signer(new byte[32].Select(_ => (byte)1).ToArray())
                      .Verify(Bolt12Constants.InvoiceRequestSignatureTag, root, new CompactPubKey(payerId.ToArray()),
                              signature);

        // Assert
        Assert.Equal("608407c18ad9a94d9ea2bcdbe170b6c20c462a7833a197621c916f78cf18e624",
                     Convert.ToHexString((byte[])root).ToLowerInvariant());
        Assert.Equal("aefe3aa88a69772c246dcaef75ed3e7566c08ecc4e9f995233526a5651fc34cd",
                     Convert.ToHexString(tagged).ToLowerInvariant());
        Assert.Equal("b8f83ea3288cfd6ea510cdb481472575141e8d8744157f98562d162cc1c472526fdb24befefbdebab4dbb726bbd1b7d8aec057f8fa805187e5950d2bbe0e5642",
                     Convert.ToHexString(signature.ToArray()).ToLowerInvariant());
        Assert.True(verified);
        Assert.Equal(bytes, Bolt12Wire.Encode(stream));
    }

    [Theory]
    [InlineData("0102")] // runs past the end
    [InlineData("0201010100")] // decreasing types
    [InlineData("fd000101ff")] // non-minimal type
    public void Given_AMalformedStream_When_Parsing_Then_FormatException(string hex)
    {
        // Act / Assert
        Assert.Throws<FormatException>(() => Bolt12Wire.ParseStream(Convert.FromHexString(hex)));
    }
}