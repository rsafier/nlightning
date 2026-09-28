namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>splicein</c>/<c>spliceout</c> (ClientCommand 33/34, splicing plan SP1-E-T1) and
/// <c>bumpsplice</c> (37, wave SPR lane SPR-B): argument parsing (usage errors before any IPC) and the printed result.
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

    [Fact]
    public void Given_ASpliceStoppedAtCommitmentSigned_When_Printed_Then_ItNamesTheSpliceAndWaitsForTheReconnection()
    {
        // Arrange: the peer disconnected after both commitment_signed, before tx_signatures; the daemon keeps the splice
        const string reason = "Splice 83c00c02dd85fdc21350d141ec1872ca0f49819936b5c7d0c931084835269fc2 stopped at "
                            + "CommitmentSigned (stopped before tx_signatures: Disconnected); it is kept and completes "
                            + "when the peer reconnects (channel_reestablish), see listchannels.";
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.CommitmentSigned,
            SpliceTxId = "83c00c02dd85fdc21350d141ec1872ca0f49819936b5c7d0c931084835269fc2",
            NewCapacitySat = 1_100_000,
            Note = reason
        };
        using var output = new StringWriter();

        // Act
        new SplicePrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Splice waiting for the peer to reconnect", printed);
        Assert.Contains("State:        CommitmentSigned", printed);
        Assert.Contains("Splice TxId:  83c00c02dd85fdc21350d141ec1872ca0f49819936b5c7d0c931084835269fc2", printed);
        Assert.Contains($"Note:         {reason}", printed);
        Assert.Contains("completes (tx_signatures,", printed);
        Assert.DoesNotContain("Reason:", printed);
        Assert.True(SpliceCommands.IsSuccess(response));
    }

    [Fact]
    public void Given_ACommitmentSignedWithAFailureReasonAndNoNote_When_Checked_Then_AFailure()
    {
        // Arrange: only the Note marks a kept splice; a FailureReason is always a failure
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.CommitmentSigned,
            SpliceTxId = "83c00c02dd85fdc21350d141ec1872ca0f49819936b5c7d0c931084835269fc2",
            FailureReason = "stopped"
        };

        // Act
        var success = SpliceCommands.IsSuccess(response);

        // Assert
        Assert.False(success);
        Assert.False(SpliceCommands.IsStoppedAtCommitmentSigned(response));
    }

    [Fact]
    public void Given_ACommitmentSignedWithoutATxId_When_Printed_Then_InProgressAndItsReason()
    {
        // Arrange: no txid, so nothing names a kept splice
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.CommitmentSigned,
            FailureReason = "stopped"
        };
        using var output = new StringWriter();

        // Act
        new SplicePrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Splice in progress", printed);
        Assert.Contains("Reason:       stopped", printed);
        Assert.False(SpliceCommands.IsSuccess(response));
    }

    [Theory]
    [InlineData(new[] { ChannelIdHex, "2604" }, 2604U, null)]
    [InlineData(new[] { ChannelIdHex, "2604", "--max-fee-sat", "5000" }, 2604U, 5000UL)]
    [InlineData(new[] { "--max-fee-sat=1", ChannelIdHex, "253" }, 253U, 1UL)]
    [InlineData(new[] { ChannelIdHex, "250000", "--max-fee-sat", "2100000000000000" }, 250000U, 2100000000000000UL)]
    public void Given_ValidBumpSpliceArguments_When_Parsed_Then_ChannelFeerateAndFeeCap(string[] args, uint feeRate,
                                                                                        ulong? maxFeeSat)
    {
        // Act
        var parsed = SpliceCommands.ParseBump(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(s_channelId, parsed.ChannelId);
        Assert.Equal(feeRate, parsed.FeeRatePerKw);
        Assert.Equal(maxFeeSat, parsed.MaxFeeSat);
        Assert.Null(ClientApp.ValidateArguments("bumpsplice", args));
        Assert.Null(ClientApp.ValidateArguments("bump-splice", args));
    }

    [Theory]
    [InlineData(new string[0], "Missing arguments.")]
    [InlineData(new[] { ChannelIdHex }, "Missing arguments.")]
    [InlineData(new[] { "abcd", "2604" }, "Invalid channel id")]
    [InlineData(new[] { ChannelIdHex, "252" }, "Invalid feerate '252'")]
    [InlineData(new[] { ChannelIdHex, "250001" }, "Invalid feerate '250001'")]
    [InlineData(new[] { ChannelIdHex, "fast" }, "Invalid feerate 'fast'")]
    [InlineData(new[] { ChannelIdHex, "2604", "extra" }, "Unexpected argument 'extra'")]
    [InlineData(new[] { ChannelIdHex, "2604", "--max-fee-sat", "0" }, "Invalid fee cap '0'")]
    [InlineData(new[] { ChannelIdHex, "2604", "--max-fee-sat", "2100000000000001" }, "Invalid fee cap")]
    [InlineData(new[] { ChannelIdHex, "2604", "--max-fee-sat" }, "Missing value for --max-fee-sat")]
    [InlineData(new[] { ChannelIdHex, "2604", "--feerate", "3000" }, "Unknown option '--feerate'")]
    public void Given_BadBumpSpliceArguments_When_Validated_Then_UsageErrorWithTheUsage(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("bumpsplice", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.Contains(SpliceCommands.BumpUsage, error);
    }

    [Fact]
    public void Given_ASignedBump_When_Printed_Then_TheNewAttemptReplacesThePreviousOne()
    {
        // Arrange
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.Signed,
            SpliceTxId = "c29f2635480831c9d0c7b5369981490fca7218ec41d15013c2fd85dd020cc083",
            NewCapacitySat = 1_099_000
        };
        using var output = new StringWriter();

        // Act
        new SplicePrinter(output, bump: true).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Splice RBF signed", printed);
        Assert.Contains("Splice TxId:  c29f2635480831c9d0c7b5369981490fca7218ec41d15013c2fd85dd020cc083", printed);
        Assert.Contains("replaces the previous one", printed);
        Assert.True(SpliceCommands.IsSuccess(response));
    }

    [Fact]
    public void Given_AnAbortedBump_When_Printed_Then_TheReasonAndAFailure()
    {
        // Arrange
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.Aborted,
            FailureReason = "tx_abort: rbf not allowed"
        };
        using var output = new StringWriter();

        // Act
        new SplicePrinter(output, bump: true).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Splice RBF aborted", printed);
        Assert.Contains("Reason:       tx_abort: rbf not allowed", printed);
        Assert.False(SpliceCommands.IsSuccess(response));
    }
}