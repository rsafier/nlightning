namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.ValueObjects;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// Wave sp1 lane SP1-G: the <c>setchannelpolicy</c>/<c>getchannelpolicy</c> arguments and their output.
/// </summary>
public class ChannelPolicyCommandTests
{
    private static readonly string s_channelIdHex = new('a', 64);

    [Fact]
    public void Given_EveryOption_When_Parsed_Then_TheRequestCarriesThem()
    {
        // Act
        var request = ChannelPolicyCommands.ParseSetOptions(
        [
            s_channelIdHex, "--fee-base-msat", "4294967295", "--fee-ppm=250", "--cltv-delta", "72",
            "--htlc-min-msat", "1000", "--htlc-max-msat=500000000"
        ], out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(new ChannelId(Convert.FromHexString(s_channelIdHex)), request.ChannelId);
        Assert.Null(request.ShortChannelId);
        Assert.Equal(uint.MaxValue, request.FeeBaseMsat);
        Assert.Equal(250u, request.FeeProportionalMillionths);
        Assert.Equal((ushort)72, request.CltvExpiryDelta);
        Assert.Equal(1_000ul, request.HtlcMinimumMsat);
        Assert.Equal(500_000_000ul, request.HtlcMaximumMsat);
        Assert.False(request.Reset);
    }

    [Theory]
    [InlineData("120x3x1")]
    [InlineData("131941395529729")]
    public void Given_AShortChannelId_When_Parsed_Then_ItIsTheBolt7Number(string channel)
    {
        // Act
        var request = ChannelPolicyCommands.ParseSetOptions([channel, "--fee-ppm", "1"], out var error);
        var get = ChannelPolicyCommands.ParseGetOptions([channel], out var getError);

        // Assert: 120 << 40 | 3 << 16 | 1
        Assert.Null(error);
        Assert.Null(getError);
        Assert.Equal((120UL << 40) | (3UL << 16) | 1UL, request!.ShortChannelId);
        Assert.Null(request.ChannelId);
        Assert.Equal((120UL << 40) | (3UL << 16) | 1UL, get!.ShortChannelId);
    }

    [Fact]
    public void Given_Reset_When_Parsed_Then_ItIsAResetWithoutValues()
    {
        // Act
        var request = ChannelPolicyCommands.ParseSetOptions([s_channelIdHex, "--reset"], out var error);

        // Assert
        Assert.Null(error);
        Assert.True(request!.Reset);
        Assert.Null(request.FeeBaseMsat);
    }

    [Theory]
    // fee_base_msat is a u32 (BOLT 7)
    [InlineData("--fee-base-msat", "4294967296")]
    [InlineData("--fee-ppm", "4294967296")]
    // cltv_expiry_delta is a u16
    [InlineData("--cltv-delta", "65536")]
    [InlineData("--htlc-max-msat", "18446744073709551616")]
    [InlineData("--htlc-min-msat", "-1")]
    [InlineData("--fee-ppm", "abc")]
    public void Given_AValueOutOfItsWireRange_When_Validated_Then_UsageError(string option, string value)
    {
        // Act
        var error = ChannelPolicyCommands.Validate("setchannelpolicy", [s_channelIdHex, option, value]);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Usage: setchannelpolicy", error);
    }

    [Theory]
    [InlineData("--fee-ppm 1")]
    [InlineData("nochannel --fee-ppm 1")]
    [InlineData("120x3x1")]
    [InlineData("120x3x1 --reset --fee-ppm 1")]
    [InlineData("120x3x1 --fee-ppm 1 --fee-ppm 2")]
    [InlineData("120x3x1 --fee-ppm")]
    [InlineData("120x3x1 --unknown 1")]
    [InlineData("120x3x1 120x3x2 --fee-ppm 1")]
    public void Given_BadArguments_When_ValidatingSet_Then_UsageError(string arguments)
    {
        // Act
        var error = ClientAppValidate("setchannelpolicy", Split(arguments));

        // Assert
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("120x3x1 120x3x2")]
    [InlineData("zz")]
    public void Given_BadArguments_When_ValidatingGet_Then_UsageError(string arguments)
    {
        // Act
        var error = ClientAppValidate("get-channel-policy", Split(arguments));

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Usage: get-channel-policy", error);
    }

    [Fact]
    public void Given_ValidArguments_When_ValidatedThroughTheApp_Then_Accepted()
    {
        // Act / Assert: both names of both commands reach the channel policy parser
        Assert.Null(ClientAppValidate("setchannelpolicy", ["120x3x1", "--htlc-max-msat", "1"]));
        Assert.Null(ClientAppValidate("set-channel-policy", [s_channelIdHex, "--reset"]));
        Assert.Null(ClientAppValidate("getchannelpolicy", ["120x3x1"]));
        Assert.Null(ClientAppValidate("get-channel-policy", [s_channelIdHex]));
    }

    [Fact]
    public void Given_APolicy_When_Printed_Then_MatchesSnapshot()
    {
        // Arrange
        var response = new ChannelPolicyIpcResponse
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0x07, 32).ToArray()),
            ShortChannelId = (120UL << 40) | (3UL << 16) | 1UL,
            FeeBaseMsat = 2_500,
            FeeProportionalMillionths = 1,
            CltvExpiryDelta = 72,
            HtlcMinimumMsat = 5_000,
            HtlcMaximumMsat = 100_000_000,
            IsFeeBaseMsatOverridden = true,
            IsCltvExpiryDeltaOverridden = true,
            IsHtlcMaximumMsatOverridden = true,
            OverrideUpdatedAt = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        };
        using var writer = new StringWriter();
        writer.NewLine = "\n";

        // Act
        new ChannelPolicyPrinter(writer).Print(response);

        // Assert
        Assert.Equal(string.Concat(new[]
        {
            "Channel Policy:",
            $"  Id:                 {Convert.ToHexStringLower(Enumerable.Repeat((byte)0x07, 32).ToArray())}",
            "  Short Channel Id:   120x3x1",
            "  Fee Base (msat):    2500 (channel)",
            "  Fee Rate (ppm):     1 (node)",
            "  CLTV Delta:         72 (channel)",
            "  HTLC Min (msat):    5000 (node)",
            "  HTLC Max (msat):    100000000 (channel)",
            "  Override Set At:    2026-09-27 10:00:00Z"
        }.Select(l => l + "\n")), writer.ToString());
    }

    [Fact]
    public void Given_AReset_When_Printed_Then_TheTitleSaysSo()
    {
        // Arrange
        var response = new ChannelPolicyIpcResponse
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0x07, 32).ToArray()),
            WasReset = true
        };
        using var writer = new StringWriter();

        // Act
        new ChannelPolicyPrinter(writer).Print(response);

        // Assert
        var output = writer.ToString();
        Assert.StartsWith("Channel Policy (reset to the node's values):", output);
        Assert.Contains("  Short Channel Id:   -", output);
        Assert.DoesNotContain("Override Set At", output);
    }

    [Fact]
    public void Given_ANodeThatDoesNotStorePolicies_When_Printed_Then_ItWarnsTheOverrideIsLostOnRestart()
    {
        // Arrange
        var response = new ChannelPolicyIpcResponse
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0x07, 32).ToArray()),
            IsMemoryOnly = true
        };
        using var writer = new StringWriter();

        // Act
        new ChannelPolicyPrinter(writer).Print(response);

        // Assert
        Assert.Contains("Warning: this node does not store channel policies", writer.ToString());
    }

    [Fact]
    public void Given_AStoredPolicy_When_Printed_Then_NoWarning()
    {
        // Arrange
        var response = new ChannelPolicyIpcResponse
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0x07, 32).ToArray())
        };
        using var writer = new StringWriter();

        // Act
        new ChannelPolicyPrinter(writer).Print(response);

        // Assert
        Assert.False(response.IsMemoryOnly);
        Assert.DoesNotContain("Warning", writer.ToString());
    }

    private static string[] Split(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string? ClientAppValidate(string cmd, string[] arguments)
    {
        Assert.True(ChannelPolicyCommands.IsChannelPolicyCommand(cmd));
        return ClientApp.ValidateArguments(cmd, arguments);
    }
}