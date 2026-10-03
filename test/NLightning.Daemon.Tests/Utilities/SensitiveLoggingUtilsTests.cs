using Microsoft.Extensions.Configuration;
using Serilog;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Utilities;

public class SensitiveLoggingUtilsTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void Given_SensitiveQueryLoggingOn_When_Checked_Then_Warns(string value)
    {
        // Arrange
        var logger = new Mock<ILogger>();
        var configuration = BuildConfiguration(value);

        // Act
        var result = SensitiveLoggingUtils.WarnIfSensitiveQueryLoggingEnabled(configuration, logger.Object);

        // Assert (SR-12)
        Assert.True(result);
        logger.Verify(x => x.Warning(It.Is<string>(m => m.Contains("preimages")),
                                     SensitiveLoggingUtils.SensitiveQueryLoggingKey), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    public void Given_SensitiveQueryLoggingOff_When_Checked_Then_DoesNotWarn(string? value)
    {
        // Arrange
        var logger = new Mock<ILogger>(MockBehavior.Strict);
        var configuration = BuildConfiguration(value);

        // Act
        var result = SensitiveLoggingUtils.WarnIfSensitiveQueryLoggingEnabled(configuration, logger.Object);

        // Assert
        Assert.False(result);
    }

    private static IConfiguration BuildConfiguration(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null)
            values[SensitiveLoggingUtils.SensitiveQueryLoggingKey] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}