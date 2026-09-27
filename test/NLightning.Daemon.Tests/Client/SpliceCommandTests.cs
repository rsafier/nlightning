namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>splicein</c>/<c>spliceout</c> (ClientCommand 33/34, splicing plan SP1-E-T1): argument parsing
/// (usage errors before any IPC) and the printed result.
/// </summary>
public class SpliceCommandTests
{
    private const string ChannelIdHex = "0101010101010101010101010101010101010101010101010101010101010101";
    private const string Address = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    private static readonly ChannelId s_channelId = new(Convert.FromHexString(ChannelIdHex));

    [Theory]
    [InlineData(new[] { ChannelIdHex, "100000" }, null)]
    [InlineData(new[] { ChannelIdHex, "100000", "--feerate", "2500" }, 2500U)]
    [InlineData(new[] { "--feerate=253", ChannelIdHex, "100000" }, 253U)]
    [InlineData(new[] { ChannelIdHex, "--feerate", "250000", "1" }, 250000U)]
    public void Given_ValidSpliceInArguments_When_Parsed_Then_ChannelAmountAndFeerate(string[] args,
                                                                                      uint? feeRate)
    {
        // Act
        var parsed = SpliceCommands.Parse(args, false, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(s_channelId, parsed.ChannelId);
        Assert.Equal(feeRate, parsed.FeeRatePerKw);
        Assert.Null(parsed.Address);
        Assert.Null(ClientApp.ValidateArguments("splicein", args));
        Assert.Null(ClientApp.ValidateArguments("splice-in", args));
    }

    [Theory]
    [InlineData(new[] { ChannelIdHex, "50000" }, null, null)]
    [InlineData(new[] { ChannelIdHex, "50000", "--address", Address }, Address, null)]
    [InlineData(new[] { "--address=" + Address, "--feerate=1000", ChannelIdHex, "50000" }, Address, 1000U)]
    public void Given_ValidSpliceOutArguments_When_Parsed_Then_TheAddressIsKept(string[] args, string? address,
                                                                                uint? feeRate)
    {
        // Act
        var parsed = SpliceCommands.Parse(args, true, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(50_000UL, parsed.AmountSat);
        Assert.Equal(address, parsed.Address);
        Assert.Equal(feeRate, parsed.FeeRatePerKw);
        Assert.Null(ClientApp.ValidateArguments("spliceout", args));
        Assert.Null(ClientApp.ValidateArguments("splice-out", args));
    }

    [Theory]
    [InlineData("splicein", new string[0], "Missing arguments.")]
    [InlineData("splicein", new[] { ChannelIdHex }, "Missing arguments.")]
    [InlineData("splicein", new[] { "abcd", "1000" }, "Invalid channel id")]
    [InlineData("splicein", new[] { ChannelIdHex, "0" }, "Invalid amount")]
    [InlineData("splicein", new[] { ChannelIdHex, "-5" }, "Invalid amount")]
    [InlineData("splicein", new[] { ChannelIdHex, "2100000000000001" }, "Invalid amount")]
    [InlineData("splicein", new[] { ChannelIdHex, "1000", "extra" }, "Unexpected argument 'extra'")]
    [InlineData("splicein", new[] { ChannelIdHex, "1000", "--feerate", "252" }, "Invalid feerate '252'")]
    [InlineData("splicein", new[] { ChannelIdHex, "1000", "--feerate", "250001" }, "Invalid feerate")]
    [InlineData("splicein", new[] { ChannelIdHex, "1000", "--feerate" }, "Missing value for --feerate")]
    [InlineData("splicein", new[] { ChannelIdHex, "1000", "--address", Address }, "Unknown option '--address'")]
    [InlineData("spliceout", new[] { ChannelIdHex, "1000", "--address=" }, "Missing value for --address")]
    [InlineData("spliceout", new[] { ChannelIdHex, "1000", "--to", Address }, "Unknown option '--to'")]
    public void Given_BadArguments_When_Validated_Then_UsageErrorWithTheUsage(string cmd, string[] args,
                                                                             string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments(cmd, args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.Contains(cmd == "splicein" ? SpliceCommands.SpliceInUsage : SpliceCommands.SpliceOutUsage, error);
    }

    [Fact]
    public void Given_ASignedSplice_When_Printed_Then_TxIdCapacityAndTheLockHint()
    {
        // Arrange
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.Signed,
            SpliceTxId = "83c00c02dd85fdc21350d141ec1872ca0f49819936b5c7d0c931084835269fc2",
            NewCapacitySat = 1_100_000
        };
        using var output = new StringWriter();

        // Act
        new SplicePrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Splice signed", printed);
        Assert.Contains($"Channel ID:   {s_channelId}", printed);
        Assert.Contains("Splice TxId:  83c00c02dd85fdc21350d141ec1872ca0f49819936b5c7d0c931084835269fc2", printed);
        Assert.Contains("New capacity: 1100000 sats", printed);
        Assert.Contains("locked at depth", printed);
        Assert.DoesNotContain("Reason", printed);
        Assert.True(SpliceCommands.IsSuccess(response));
    }

    [Fact]
    public void Given_AnAbortedSplice_When_Printed_Then_TheReasonAndAFailure()
    {
        // Arrange
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.Aborted,
            FailureReason = "tx_abort: feerate too low"
        };
        using var output = new StringWriter();

        // Act
        new SplicePrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Splice aborted", printed);
        Assert.Contains("Reason:       tx_abort: feerate too low", printed);
        Assert.DoesNotContain("Splice TxId", printed);
        Assert.False(SpliceCommands.IsSuccess(response));
    }

    [Fact]
    public void Given_ASpliceStillNegotiating_When_Printed_Then_InProgressAndNotAFailure()
    {
        // Arrange
        var response = new SpliceIpcResponse { ChannelId = s_channelId, State = SpliceNegotiationState.Negotiating };
        using var output = new StringWriter();

        // Act
        new SplicePrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Splice in progress", printed);
        Assert.Contains("goes on in the daemon", printed);
        Assert.True(SpliceCommands.IsSuccess(response));
    }
}