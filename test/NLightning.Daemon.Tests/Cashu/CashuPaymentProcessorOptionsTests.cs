using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        var options = new CashuPaymentProcessorOptions { Enabled = true, AllowInsecureLoopback = true };

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

    [Fact]
    public void Given_ALoopbackListenerWithoutClientAuthentication_When_Validated_Then_RefusedUnlessAllowed()
    {
        // Arrange (NL-998): h2c, or TLS without ca.pem, lets every local process pay from the node
        var tls = TlsDirectory(withCa: false);
        var options = new CashuPaymentProcessorOptions { Enabled = true };
        var serverOnlyTls = new CashuPaymentProcessorOptions { Enabled = true, TlsDirectory = tls };

        // Act
        var plain = options.GetValidationErrors(isMainnet: false);
        var serverOnly = serverOnlyTls.GetValidationErrors(isMainnet: false);
        options.AllowInsecureLoopback = true;
        serverOnlyTls.AllowInsecureLoopback = true;

        // Assert
        Assert.Contains(plain, e => e.Contains("no client authentication") && e.Contains("AllowInsecureLoopback"));
        Assert.Contains(serverOnly, e => e.Contains("no ca.pem"));
        Assert.Empty(options.GetValidationErrors(isMainnet: false));
        Assert.Empty(serverOnlyTls.GetValidationErrors(isMainnet: false));
    }

    [Fact]
    public void Given_ANonLoopbackListener_When_ItsTlsHasNoCa_Then_RefusedEvenWithInsecureLoopbackAllowed()
    {
        // Arrange (NL-998)
        var options = new CashuPaymentProcessorOptions
        {
            Enabled = true,
            ListenAddress = "0.0.0.0",
            TlsDirectory = TlsDirectory(withCa: false),
            AllowInsecureLoopback = true
        };

        // Act
        var withoutCa = options.GetValidationErrors(isMainnet: false);
        options.TlsDirectory = TlsDirectory(withCa: true);
        var withCa = options.GetValidationErrors(isMainnet: false);

        // Assert
        Assert.Contains(withoutCa, e => e.Contains("not loopback") && e.Contains("ca.pem"));
        Assert.Empty(withCa);
    }

    [Fact]
    public void Given_NoNodeOptions_When_AnEnabledProcessorIsValidated_Then_CheckedAsMainnet()
    {
        // Arrange (NL-998): without the node's network the mainnet gate stays closed
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cashu:PaymentProcessor:Enabled"] = "true",
            ["Cashu:PaymentProcessor:AllowInsecureLoopback"] = "true"
        }).Build();
        using var provider = new ServiceCollection().AddCashuPaymentProcessor(configuration).BuildServiceProvider();

        // Act
        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CashuPaymentProcessorOptions>>().Value);

        // Assert
        Assert.Contains("AllowMainnet", error.Message);
    }

    /// <summary>A directory with placeholder <c>server.pem</c>/<c>server.key</c> and, optionally, <c>ca.pem</c>.</summary>
    private static string TlsDirectory(bool withCa)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nltg-cashu-tls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        foreach (var file in withCa ? new[] { "server.pem", "server.key", "ca.pem" } : ["server.pem", "server.key"])
            File.WriteAllText(Path.Combine(directory, file), "placeholder");
        return directory;
    }
}