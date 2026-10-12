namespace NLightning.Domain.Tests.Gossip;

using Domain.Gossip.Addresses;

public class OnionV3AddressTests
{
    // DuckDuckGo's onion service (rend-spec-v3 address with checksum 9164)
    private const string DuckDuckGo = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    private const string DuckDuckGoBytes =
        "1d04a1d04a338c6e6ae970bfabee49049d6702250984ca950c01673f4ec034ad916403";

    [Fact]
    public void Given_ARealOnionAddress_When_Parsed_Then_ItIsValidWithItsChecksum()
    {
        // Act
        var ok = OnionV3Address.TryParse(DuckDuckGo, out var address, out var error);

        // Assert
        Assert.True(ok, error);
        Assert.Equal(DuckDuckGoBytes, Convert.ToHexStringLower(address));
        Assert.True(OnionV3Address.IsValid(address));
        Assert.Equal(DuckDuckGo, OnionV3Address.ToHostName(address));
    }

    [Fact]
    public void Given_ThePublicKey_When_TheAddressIsDerived_Then_ItIsTheOnionAddress()
    {
        // Arrange
        var publicKey = Convert.FromHexString(DuckDuckGoBytes)[..32];

        // Act
        var address = OnionV3Address.FromPublicKey(publicKey);

        // Assert
        Assert.Equal(DuckDuckGoBytes, Convert.ToHexStringLower(address));
    }

    [Theory]
    [InlineData("DUCKDUCKGOGG42XJOC72X3SJASOWOARFBGCMVFIMAFTT6TWAGSWZCZAD.ONION")]
    [InlineData("duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad")]
    public void Given_UpperCaseOrNoSuffix_When_Parsed_Then_ItIsAccepted(string host)
    {
        // Act
        var ok = OnionV3Address.TryParse(host, out var address, out _);

        // Assert
        Assert.True(ok);
        Assert.Equal(DuckDuckGo, OnionV3Address.ToHostName(address));
    }

    [Theory]
    [InlineData("duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczae.onion", "version 4")]
    [InlineData("euckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion", "checksum")]
    [InlineData("3g2upl4pq6kufc4m.onion", "v2")]
    [InlineData("duckduckgo.onion", "not a Tor v3")]
    [InlineData("duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzcza1.onion", "base32")]
    [InlineData("", "empty")]
    public void Given_ABadOnionAddress_When_Parsed_Then_ItIsRefusedWithTheReason(string host, string reason)
    {
        // Act
        var ok = OnionV3Address.TryParse(host, out _, out var error);

        // Assert
        Assert.False(ok);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Given_AnAddressWithAnotherVersion_When_Validated_Then_ItIsInvalid()
    {
        // Arrange
        var address = Convert.FromHexString(DuckDuckGoBytes);
        address[34] = 4;

        // Act & Assert
        Assert.False(OnionV3Address.IsValid(address));
        Assert.False(OnionV3Address.TryParse(OnionV3Address.ToHostName(address), out _, out var error));
        Assert.Contains("version 4", error);
    }
}