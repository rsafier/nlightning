namespace NLightning.Daemon.Tests.Cashu;

using NLightning.Cashu.PaymentProcessor;

/// <summary>
/// The start-up checks of <c>Cashu:PaymentProcessor</c> (Cashu plan C1, NL-992).
/// </summary>
public class CashuPaymentProcessorOptionsTests
{
    [Fact]
    public void Given_TheDefaults_When_Validated_Then_DisabledAndValidEvenOnMainnet()
    {
        // Act
        var errors = new CashuPaymentProcessorOptions().GetValidationErrors(isMainnet: true);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_AnEnabledProcessorOnMainnet_When_Validated_Then_RefusedUnlessAllowed()
    {
        // Arrange
        var options = new CashuPaymentProcessorOptions { Enabled = true };

        // Act
        var refused = options.GetValidationErrors(isMainnet: true);
        options.AllowMainnet = true;
        var allowed = options.GetValidationErrors(isMainnet: true);

        // Assert
        Assert.Contains(refused, e => e.Contains("AllowMainnet"));
        Assert.Empty(allowed);
    }

    [Theory]
    [InlineData("0.0.0.0", null, "TlsDirectory")]
    [InlineData("not-an-ip", null, "not an IP address")]
    [InlineData("127.0.0.1", "/nonexistent-tls-dir", "server.pem")]
    public void Given_AnUnsafeOrBrokenListener_When_Validated_Then_Refused(string address, string? tlsDirectory,
                                                                          string expected)
    {
        // Arrange
        var options = new CashuPaymentProcessorOptions
        {
            Enabled = true,
            ListenAddress = address,
            TlsDirectory = tlsDirectory
        };

        // Act
        var errors = options.GetValidationErrors(isMainnet: false);

        // Assert
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Given_AnUnknownUnit_When_Validated_Then_Refused()
    {
        // Arrange
        var options = new CashuPaymentProcessorOptions { Enabled = true, Unit = "usd" };

        // Act & Assert
        Assert.Contains(options.GetValidationErrors(isMainnet: false), e => e.Contains("Unit"));
    }
}