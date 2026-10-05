using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.LnBackend.Tests;

using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The backend's own settings (<c>LnBackend</c>, NL-1148): the listener rules and the mainnet refusal. An enabled
/// backend serves an operator's hold invoices to every local process, so insecure settings must be explicit and
/// mainnet must be opted into.
/// </summary>
public sealed class LnBackendOptionsTests
{
    /// <summary>The listener settings of an enabled, insecure-loopback backend (port 0: pick one).</summary>
    private static LnBackendOptions Valid() => new()
    {
        Enabled = true,
        Port = 0,
        AllowInsecureLoopback = true
    };

    [Fact]
    public void Given_InsecureLoopback_When_Validated_Then_ThereIsNoError()
    {
        // Act
        var errors = Valid().GetValidationErrors();

        // Assert: h2c on loopback with the explicit opt-in is a supported single-user setup
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_NoTlsAndNoOptIn_When_Validated_Then_TheMissingChoiceIsReported()
    {
        // Arrange
        var options = Valid();
        options.AllowInsecureLoopback = false;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        var error = Assert.Single(errors);
        Assert.StartsWith("LnBackend:TlsDirectory is required (or set LnBackend:AllowInsecureLoopback",
                          error, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_AnInsecureNonLoopbackListener_When_Validated_Then_ItIsReported()
    {
        // Arrange: every local process of every user, not just the operator's
        var options = Valid();
        options.ListenAddress = "0.0.0.0";

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        var error = Assert.Single(errors);
        Assert.StartsWith("LnBackend:AllowInsecureLoopback needs a loopback ListenAddress", error,
                          StringComparison.Ordinal);
    }

    [Fact]
    public void Given_Tls_When_Validated_Then_TheListenerMayBeOffLoopback()
    {
        // Arrange: a certificate directory makes any address a valid listener
        var options = Valid();
        options.AllowInsecureLoopback = false;
        options.TlsDirectory = Path.GetTempPath();
        options.ListenAddress = "0.0.0.0";

        // Act & Assert
        Assert.Empty(options.GetValidationErrors());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Given_APortThatIsNoTcpPort_When_Validated_Then_ItIsReported(int port)
    {
        // Arrange
        var options = Valid();
        options.Port = port;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Single(errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void Given_AConnectionLimitOutOfRange_When_Validated_Then_ItIsReported(int maxConnections)
    {
        // Arrange
        var options = Valid();
        options.MaxConnections = maxConnections;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Single(errors);
    }

    [Fact]
    public void Given_Mainnet_When_ValidatedByTheValidator_Then_TheBackendIsRefused()
    {
        // Act
        var result = ValidateOn(BitcoinNetwork.Mainnet, Valid());

        // Assert: an ASP backend moves real money on the operator's behalf
        Assert.True(result.Failed);
        Assert.Contains(result.Failures,
                        f => f.StartsWith("LnBackend is refused on mainnet unless LnBackend:AllowMainnet",
                                          StringComparison.Ordinal));
    }

    [Fact]
    public void Given_MainnetWithAllowMainnet_When_ValidatedByTheValidator_Then_ItPasses()
    {
        // Arrange
        var options = Valid();
        options.AllowMainnet = true;

        // Act & Assert
        Assert.True(ValidateOn(BitcoinNetwork.Mainnet, options).Succeeded);
    }

    [Fact]
    public void Given_Regtest_When_ValidatedByTheValidator_Then_ItPasses()
    {
        // Act & Assert
        Assert.True(ValidateOn(BitcoinNetwork.Regtest, Valid()).Succeeded);
    }

    [Fact]
    public void Given_ADisabledBackend_When_ValidatedByTheValidator_Then_EvenInvalidSettingsPass()
    {
        // Arrange: nothing listens, so the settings are never used
        var options = new LnBackendOptions { Enabled = false };

        // Act & Assert
        Assert.True(ValidateOn(BitcoinNetwork.Mainnet, options).Succeeded);
    }

    /// <summary>Runs the options validator the way <c>AddLnBackend</c> registers it, on the given network.</summary>
    private static ValidateOptionsResult ValidateOn(BitcoinNetwork network, LnBackendOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<NodeOptions>>(
            Options.Create(new NodeOptions { BitcoinNetwork = network }));
        services.AddLnBackend(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IValidateOptions<LnBackendOptions>>().Validate(null, options);
    }
}