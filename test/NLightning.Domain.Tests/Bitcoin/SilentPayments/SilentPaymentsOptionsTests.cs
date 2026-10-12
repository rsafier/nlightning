namespace NLightning.Domain.Tests.Bitcoin.SilentPayments;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.SilentPayments;

public class SilentPaymentsOptionsTests
{
    [Fact]
    public void Given_DefaultConfiguration_When_ValidatedOnMainnet_Then_DisabledWithoutOpeningGate()
    {
        // Arrange
        var options = new SilentPaymentsOptions();
        // Act
        var errors = options.GetValidationErrors(isMainnet: true);
        // Assert
        Assert.False(options.Enabled);
        Assert.False(options.AllowMainnet);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Given_EnabledSendingOrReceiving_When_MainnetValidated_Then_ExplicitPermissionIsRequired(bool send, bool receive)
    {
        // Arrange
        var options = new SilentPaymentsOptions { Enabled = true, Send = send, Receive = receive };
        // Act
        var refused = options.GetValidationErrors(isMainnet: true);
        options.AllowMainnet = true;
        var allowed = options.GetValidationErrors(isMainnet: true);
        // Assert
        Assert.Contains(refused, error => error.Contains("AllowMainnet", StringComparison.Ordinal));
        Assert.Empty(allowed);
    }

    [Fact]
    public void Given_EnabledNonMainnetConfiguration_When_Validated_Then_NoMainnetPermissionIsRequired()
    {
        // Arrange
        var options = new SilentPaymentsOptions { Enabled = true };
        // Act / Assert
        Assert.Empty(options.GetValidationErrors(isMainnet: false));
    }

    [Fact]
    public void Given_UnknownPrevoutSource_When_Validated_Then_RefusedRatherThanSilentlySelectingAnotherSource()
    {
        // Arrange
        var options = new SilentPaymentsOptions { PrevoutSource = (SilentPaymentPrevoutSource)999 };
        // Act / Assert
        Assert.Contains(options.GetValidationErrors(), error => error.Contains("unknown prevout source", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1, 1_000)]
    [InlineData(546, -1)]
    public void Given_NegativeAmountMinimum_When_Validated_Then_Refused(long sendMinimum, long receiveMinimum)
    {
        // Arrange
        var options = new SilentPaymentsOptions { MinSendSat = sendMinimum, MinReceiveSat = receiveMinimum };
        // Act / Assert
        Assert.Contains(options.GetValidationErrors(), error => error.Contains("minimum amounts", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100_001, 100)]
    [InlineData(1_000, -1)]
    [InlineData(1_000, 100_001)]
    public void Given_UnboundedLabelConfiguration_When_Validated_Then_Refused(int maxLabels, int recoveryLabels)
    {
        // Arrange
        var options = new SilentPaymentsOptions { MaxLabels = maxLabels, RecoveryLabelCount = recoveryLabels };
        // Act / Assert
        Assert.Contains(options.GetValidationErrors(), error => error.Contains("100000", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_ZeroMinimumAndMaximumLabelBoundary_When_Validated_Then_IntentionalRecoveryConfigurationIsAllowed()
    {
        // Arrange
        var options = new SilentPaymentsOptions { MinSendSat = 0, MinReceiveSat = 0, MaxLabels = 100_000, RecoveryLabelCount = 100_000 };
        // Act / Assert
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_NegativeRescanRate_When_Validated_Then_Refused()
    {
        // Arrange
        var options = new SilentPaymentsOptions { RescanBlocksPerSecond = -1 };
        // Act / Assert
        Assert.Contains(options.GetValidationErrors(), error => error.Contains("RescanBlocksPerSecond", StringComparison.Ordinal));
    }
}