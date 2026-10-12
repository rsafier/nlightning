using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Functions;

using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;

public class Secp256K1MathTests
{
    // secp256k1 curve order n
    private static readonly byte[] s_curveOrder =
        Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");

    private static readonly byte[] s_curveOrderMinusOne =
        Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364140");

    private static readonly byte[] s_k = Enumerable.Repeat((byte)0x41, 32).ToArray();
    private static readonly byte[] s_bf = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private readonly Secp256K1Math _math = new();

    public static TheoryData<string> OutOfRangeScalars => new()
    {
        "0000000000000000000000000000000000000000000000000000000000000000",
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", // n
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364142", // n + 1
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
    };

    public static TheoryData<string> InvalidScalars => new()
    {
        "0000000000000000000000000000000000000000000000000000000000000000",
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", // n
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364142", // n + 1
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
        "01", // too short
        "000000000000000000000000000000000000000000000000000000000000000001" // too long
    };

    [Fact]
    public void Given_ScalarsKAndBf_When_MultiplyingPubKeyAndPrivKey_Then_KTimesBfGEqualsKBfTimesG()
    {
        // Arrange
        CompactPubKey bfG = GetPubKey(s_bf);

        // Act
        var lhs = _math.MultiplyPubKey(bfG, s_k); // k * (bf * G)
        var kbf = _math.MultiplyPrivKey(s_k, s_bf); // k * bf mod n
        CompactPubKey rhs = GetPubKey(kbf); // (k * bf) * G

        // Assert
        Assert.Equal(rhs, lhs);
    }

    [Fact]
    public void Given_PubKeyAndScalar_When_MultiplyPubKey_Then_EqualsNBitcoinEcdhPoint()
    {
        // Arrange
        CompactPubKey bfG = GetPubKey(s_bf);
        using var key = new Key(s_k);
        var expected = new PubKey(bfG).GetSharedPubkey(key).Compress().ToBytes();

        // Act
        var result = _math.MultiplyPubKey(bfG, s_k);

        // Assert
        Assert.Equal(expected, (byte[])result);
    }

    [Fact]
    public void Given_TwoPrivKeys_When_AddingPrivAndPubKeys_Then_ResultsAreConsistent()
    {
        // Arrange
        CompactPubKey kG = GetPubKey(s_k);
        CompactPubKey bfG = GetPubKey(s_bf);

        // Act
        var sumPriv = _math.AddPrivKeys(s_k, s_bf);
        var sumPub = _math.AddPubKeys(kG, bfG);

        // Assert
        Assert.Equal(GetPubKey(sumPriv), (byte[])sumPub);
    }

    [Fact]
    public void Given_CurveOrderMinusOne_When_MultiplyPrivKeyByItself_Then_ResultIsOne()
    {
        // Arrange
        var expected = new byte[32];
        expected[31] = 1;

        // Act ((n-1)^2 = (-1)^2 = 1 mod n)
        var result = _math.MultiplyPrivKey(s_curveOrderMinusOne.ToArray(), s_curveOrderMinusOne);

        // Assert
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [MemberData(nameof(InvalidScalars))]
    public void Given_InvalidScalar_When_MultiplyPubKey_Then_ThrowsArgumentException(string scalarHex)
    {
        // Arrange
        CompactPubKey bfG = GetPubKey(s_bf);
        var scalar = Convert.FromHexString(scalarHex);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _math.MultiplyPubKey(bfG, scalar));
    }

    [Theory]
    [MemberData(nameof(InvalidScalars))]
    public void Given_InvalidScalar_When_MultiplyPrivKey_Then_ThrowsArgumentException(string scalarHex)
    {
        // Arrange
        var scalar = Convert.FromHexString(scalarHex);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _math.MultiplyPrivKey(s_k, scalar));
    }

    [Theory]
    [MemberData(nameof(OutOfRangeScalars))]
    public void Given_OutOfRangePrivKey_When_MultiplyPrivKey_Then_ThrowsArgumentException(string scalarHex)
    {
        // Arrange
        var scalar = Convert.FromHexString(scalarHex);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _math.MultiplyPrivKey(scalar, s_bf));
    }

    [Fact]
    public void Given_CurveOrderAsSecondPrivKey_When_AddPrivKeys_Then_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _math.AddPrivKeys(s_k, s_curveOrder.ToArray()));
    }

    [Fact]
    public void Given_PrivKeyAndItsNegation_When_AddPrivKeys_Then_ThrowsInvalidOperationException()
    {
        // Arrange (1 + (n-1) = 0 mod n)
        var one = new byte[32];
        one[31] = 1;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _math.AddPrivKeys(one, s_curveOrderMinusOne.ToArray()));
    }

    [Fact]
    public void Given_PubKeyAndItsNegation_When_AddPubKeys_Then_ThrowsInvalidOperationException()
    {
        // Arrange (P + (-P) = infinity)
        var p = GetPubKey(s_k);
        var negP = p.ToArray();
        negP[0] = p[0] == 0x02 ? (byte)0x03 : (byte)0x02;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _math.AddPubKeys(p, negP));
    }

    [Fact]
    public void Given_PubKeyNotOnCurve_When_MultiplyPubKey_Then_ThrowsArgumentException()
    {
        // Arrange (x = 0 is not a valid x coordinate on secp256k1: 7 is a quadratic non-residue mod p)
        var invalid = new byte[33];
        invalid[0] = 0x02;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _math.MultiplyPubKey(invalid, s_k));
    }

    private static byte[] GetPubKey(byte[] privKey)
    {
        using var key = new Key(privKey);
        return key.PubKey.Compress().ToBytes();
    }
}