namespace NLightning.Daemon.Tests.Client;

using NLightning.Client;
using NLightning.Client.Handlers;
using Domain.Client.Enums;

public class SilentPaymentCommandTests
{
    [Fact]
    public void Given_LabeledAddressRequest_When_Parsed_Then_LabelIsKeptAndArgumentsValidate()
    {
        // Arrange / Act
        var request = SilentPaymentCommands.Parse("getspaddress", ["--label", "Coffee shop"], out var error);
        // Assert
        Assert.Null(error);
        Assert.Equal("Coffee shop", request!.Label);
        Assert.Null(ClientApp.ValidateArguments("getspaddress", ["--label", "Coffee shop"]));
        Assert.Null(ClientApp.ValidateArguments("getspaddress", []));
    }

    [Fact]
    public void Given_RescanRangeOrCancel_When_Parsed_Then_RecoveryOptionsAreKept()
    {
        // Arrange / Act
        var scan = SilentPaymentCommands.Parse("sprescan", ["--labels", "100", "--from-height", "0"], out var error);
        var cancel = SilentPaymentCommands.Parse("sprescan", ["--cancel"], out var cancelError);
        // Assert
        Assert.Null(error);
        Assert.Equal((uint)0, scan!.FromHeight);
        Assert.Equal((uint)100, scan.RecoveryLabels);
        Assert.False(scan.Cancel);
        Assert.Null(cancelError);
        Assert.True(cancel!.Cancel);
        Assert.Null(cancel.FromHeight);
        Assert.Null(ClientApp.ValidateArguments("sprescan", ["--from-height", "4294967295", "--labels", "0"]));
    }

    [Theory]
    [InlineData("splabels")]
    [InlineData("spstatus")]
    public void Given_ReadOnlyCommand_When_Validated_Then_NoArgumentsAreAccepted(string command)
    {
        // Arrange / Act / Assert
        Assert.Null(ClientApp.ValidateArguments(command, []));
        Assert.NotNull(ClientApp.ValidateArguments(command, ["--label", "store"]));
    }

    [Theory]
    [InlineData("getspaddress", "--label")]
    [InlineData("getspaddress", "--label --label")]
    [InlineData("getspaddress", "--label --cancel")]
    [InlineData("getspaddress", "--label store --label again")]
    [InlineData("getspaddress", "--unknown")]
    [InlineData("sprescan", "")]
    [InlineData("sprescan", "--cancel --cancel")]
    [InlineData("sprescan", "--from-height 1 --from-height 2")]
    [InlineData("sprescan", "--from-height -1")]
    [InlineData("sprescan", "--from-height 4294967296")]
    [InlineData("sprescan", "--from-height 1 --labels 100001")]
    [InlineData("sprescan", "--from-height 1 --labels 2 --labels 3")]
    [InlineData("sprescan", "--cancel --from-height 1")]
    [InlineData("sprescan", "--cancel --labels 1")]
    [InlineData("sprescan", "--labels 1")]
    [InlineData("sprescan", "--from-height 1 extra")]
    public void Given_MalformedOrDuplicateOptions_When_Validated_Then_UsageError(string command, string options)
    {
        // Arrange
        var args = options.Length == 0 ? [] : options.Split(' ');
        // Act
        var error = ClientApp.ValidateArguments(command, args);
        // Assert
        Assert.NotNull(error);
        Assert.Contains("Usage:", error);
    }

    [Fact]
    public void Given_SilentPaymentCommands_When_Numbered_Then_WireIdsMatchPlan()
    {
        // Arrange / Act / Assert
        Assert.Equal(52, (int)ClientCommand.GetSilentPaymentAddress);
        Assert.Equal(53, (int)ClientCommand.SilentPaymentLabels);
        Assert.Equal(54, (int)ClientCommand.SilentPaymentRescan);
        Assert.Equal(55, (int)ClientCommand.SilentPaymentStatus);
    }
}