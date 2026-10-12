using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Services.Ipc;

using Daemon.Services.Ipc;

public class CookieFileAuthenticatorTests : IDisposable
{
    private const string Cookie = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _directory;
    private readonly string _cookiePath;

    public CookieFileAuthenticatorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nltg-cookie-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _cookiePath = Path.Combine(_directory, "nltg.cookie");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Given_TheCookie_When_ValidateAsync_Then_Accepts()
    {
        // Arrange: the file may end with a newline (written by hand or by another tool)
        await File.WriteAllTextAsync(_cookiePath, Cookie + "\n", TestContext.Current.CancellationToken);
        var authenticator = CreateAuthenticator();

        // Act
        var valid = await authenticator.ValidateAsync(Cookie, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(valid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdee")] // last char differs
    [InlineData("0123456789abcdef")] // a prefix
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00")] // longer
    public async Task Given_AnotherToken_When_ValidateAsync_Then_Refuses(string? token)
    {
        // Arrange
        await File.WriteAllTextAsync(_cookiePath, Cookie, TestContext.Current.CancellationToken);
        var authenticator = CreateAuthenticator();

        // Act
        var valid = await authenticator.ValidateAsync(token, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(valid);
    }

    [Fact]
    public async Task Given_NoCookieFile_When_ValidateAsync_Then_Refuses()
    {
        // Arrange: the daemon deletes the cookie when it stops
        var authenticator = CreateAuthenticator();

        // Act
        var valid = await authenticator.ValidateAsync(Cookie, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(valid);
    }

    [Fact]
    public async Task Given_AnEmptyCookieFile_When_ValidateAsyncWithAnEmptyToken_Then_Refuses()
    {
        // Arrange
        await File.WriteAllTextAsync(_cookiePath, "\n", TestContext.Current.CancellationToken);
        var authenticator = CreateAuthenticator();

        // Act
        var valid = await authenticator.ValidateAsync(" ", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(valid);
    }

    private CookieFileAuthenticator CreateAuthenticator()
    {
        return new CookieFileAuthenticator(_cookiePath, NullLogger<CookieFileAuthenticator>.Instance);
    }
}