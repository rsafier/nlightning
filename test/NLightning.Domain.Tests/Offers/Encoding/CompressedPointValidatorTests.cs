namespace NLightning.Domain.Tests.Offers.Encoding;

using Domain.Offers.Encoding;

public class CompressedPointValidatorTests
{
    [Theory]
    // secp256k1 generator G
    [InlineData("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", true)]
    [InlineData("0379be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", true)]
    // offers-test.json keys
    [InlineData("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619", true)]
    [InlineData("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c", true)]
    [InlineData("020202020202020202020202020202020202020202020202020202020202020202", true)]
    // offers-test.json "bad" keys: x^3 + 7 is not a square
    [InlineData("030303030303030303030303030303030303030303030303030303030303030303", false)]
    [InlineData("020303030303030303030303030303030303030303030303030303030303030303", false)]
    // prefix, length, x >= p
    [InlineData("0479be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", false)]
    [InlineData("0079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", false)]
    [InlineData("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f817", false)]
    [InlineData("02fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f", false)]
    [InlineData("", false)]
    public void Given_Bytes_When_Checking_Then_OnlyCompressedCurvePointsPass(string hex, bool valid)
    {
        // Act & Assert
        Assert.Equal(valid, CompressedPointValidator.IsValid(Convert.FromHexString(hex)));
    }
}