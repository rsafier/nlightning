namespace NLightning.Infrastructure.Tests.Crypto.Hashes;

using Infrastructure.Crypto.Hashes;

public class Argon2IdTests
{
    private const string Password = "password";
    private static readonly byte[] s_salt = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");

    [Fact]
    public void Given_DefaultParameters_When_DeriveKey_Then_Uses64MiBAndDiffersFromLegacy64KiB()
    {
        // Arrange
        using var argon2Id = new Argon2Id();
        var defaultKey = new byte[32];
        var explicitKey = new byte[32];
        var legacyKey = new byte[32];

        // Act
        argon2Id.DeriveKeyFromPasswordAndSalt(Password, s_salt, defaultKey);
        argon2Id.DeriveKeyFromPasswordAndSalt(Password, s_salt, explicitKey, 3, 64UL * 1024 * 1024);
        argon2Id.DeriveKeyFromPasswordAndSalt(Password, s_salt, legacyKey, 3, 64UL * 1024);

        // Assert
        Assert.Equal(64UL * 1024 * 1024, Argon2Id.DefaultMemLimit);
        Assert.Equal(explicitKey, defaultKey);
        Assert.NotEqual(legacyKey, defaultKey);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(0)]
    public void Given_WrongSaltLength_When_DeriveKey_Then_Throws(int saltLength)
    {
        // Arrange
        using var argon2Id = new Argon2Id();
        var key = new byte[32];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => argon2Id.DeriveKeyFromPasswordAndSalt(Password, new byte[saltLength],
                                                                                         key));
    }

    [Fact]
    public void Given_MemLimitBelowMinimum_When_DeriveKey_Then_Throws()
    {
        // Arrange
        using var argon2Id = new Argon2Id();
        var key = new byte[32];

        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => argon2Id.DeriveKeyFromPasswordAndSalt(Password, s_salt, key, 3, 1024));
    }
}