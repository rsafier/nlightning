namespace NLightning.Daemon.Tests.Client;

using NLightning.Client;

/// <summary>
/// The <c>keysend</c> command line (lane lh1-l3): node id, amount in sats, <c>--tlv type=hex</c> custom records,
/// <c>--max-fee-msat</c> and <c>--timeout</c>.
/// </summary>
public class ClientAppKeysendTests
{
    private const string NodeId = "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c";

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void Given_AnOperatorPreimageFile_When_Parsed_Then_OnlyExactly32BytesAreAccepted(int length)
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = Enumerable.Range(0, length).Select(value => (byte)value).ToArray();
            File.WriteAllBytes(path, bytes);

            var parsed = ClientApp.ParseKeysendOptions([NodeId, "100", "--preimage-file", path], out var error);

            if (length == 32)
            {
                Assert.Null(error);
                Assert.NotNull(parsed);
                Assert.Equal(bytes, (byte[])parsed.Preimage!.Value);
            }
            else
            {
                Assert.Null(parsed);
                Assert.Contains("exactly 32 raw bytes", error);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Given_ADuplicatePreimageFile_When_Parsed_Then_ItIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[32]);
            var parsed = ClientApp.ParseKeysendOptions(
                [NodeId, "100", "--preimage-file", path, "--preimage-file", path], out var error);
            Assert.Null(parsed);
            Assert.Contains("twice", error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Given_KeysendWithOptions_When_Parsed_Then_EveryValueIsRead()
    {
        // Arrange
        string[] args =
        [
            NodeId, "2100", "--tlv", "7629169=7b7d", "--tlv=65536=", "--max-fee-msat", "5000", "--timeout=90"
        ];

        // Act
        var parsed = ClientApp.ParseKeysendOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(NodeId, Convert.ToHexStringLower(parsed.Destination));
        Assert.Equal(2100UL, parsed.AmountSat);
        Assert.Equal(new byte[] { 0x7b, 0x7d }, parsed.CustomRecords[7629169]);
        Assert.Empty(parsed.CustomRecords[65536]);
        Assert.Equal(5000UL, parsed.MaxFeeMsat);
        Assert.Equal(90u, parsed.TimeoutSeconds);
        Assert.Null(ClientApp.ValidateArguments("keysend", args));
    }

    public static TheoryData<string[]> InvalidArguments => new(
        [NodeId], // no amount
        ["02abcd", "10"], // bad node id
        [NodeId, "0"], // zero amount
        [NodeId, "10", "extra"],
        [NodeId, "10", "--tlv", "65535=01"], // below the custom range
        [NodeId, "10", "--tlv", "5482373484=01"], // the keysend preimage
        [NodeId, "10", "--tlv", "65537=0"], // odd hex
        [NodeId, "10", "--tlv", "65537"], // no value
        [NodeId, "10", "--tlv", "65537=01", "--tlv", "65537=02"], // twice
        [NodeId, "10", "--timeout", "301"],
        [NodeId, "10", "--max-parts", "2"]); // keysend never splits

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Given_InvalidKeysendArguments_When_Validated_Then_UsageError(string[] args)
    {
        // Act
        var error = ClientApp.ValidateArguments("keysend", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Usage: keysend", error);
    }
}