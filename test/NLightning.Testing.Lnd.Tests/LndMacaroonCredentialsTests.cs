using Grpc.Core;

namespace NLightning.Testing.Lnd.Tests;

public class LndMacaroonCredentialsTests
{
    [Fact]
    public async Task Given_AMacaroon_When_TheInterceptorRuns_Then_ItAddsTheLowerCaseHexMacaroonHeader()
    {
        // Arrange
        var interceptor = LndMacaroonCredentials.CreateInterceptor([0x02, 0x01, 0x0A, 0xBC, 0xFF]);
        var metadata = new Metadata();

        // Act
        await interceptor(new AuthInterceptorContext("https://h:1/lnrpc.Lightning", "GetInfo"), metadata);

        // Assert
        var entry = Assert.Single(metadata);
        Assert.Equal("macaroon", entry.Key);
        Assert.Equal("02010abcff", entry.Value);
    }

    [Fact]
    public void Given_AnEmptyMacaroon_When_CreatingTheInterceptor_Then_ArgumentException()
    {
        // Act
        var exception = Record.Exception(() => LndMacaroonCredentials.CreateInterceptor([]));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void Given_AMacaroon_When_CreatingCallCredentials_Then_TheyExist()
    {
        // Act
        var credentials = LndMacaroonCredentials.Create([0x02]);

        // Assert
        Assert.NotNull(credentials);
    }
}