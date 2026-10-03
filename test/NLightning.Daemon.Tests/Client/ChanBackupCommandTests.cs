namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.ValueObjects;
using NLightning.Client;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

public class ChanBackupCommandTests
{
    private static readonly string s_channelHex = new('1', 64);

    [Theory]
    [InlineData("exportchanbackup")]
    [InlineData("export-chan-backup")]
    public void Given_ExportChanBackupArguments_When_Validated_Then_ChannelAndOutputAreAccepted(string command)
    {
        // Act / Assert
        Assert.Null(ClientApp.ValidateArguments(command, []));
        Assert.Null(ClientApp.ValidateArguments(command, [s_channelHex]));
        Assert.Null(ClientApp.ValidateArguments(command, ["--output", "/tmp/x.backup"]));
        Assert.Null(ClientApp.ValidateArguments(command, ["--output=/tmp/x.backup", s_channelHex]));
        Assert.NotNull(ClientApp.ValidateArguments(command, ["--output"]));
        Assert.NotNull(ClientApp.ValidateArguments(command, ["--output="]));
        Assert.NotNull(ClientApp.ValidateArguments(command, ["nothex"]));
        Assert.NotNull(ClientApp.ValidateArguments(command, [s_channelHex, s_channelHex]));
    }

    [Fact]
    public void Given_ExportArguments_When_Parsed_Then_TheChannelAndTheFileAreRead()
    {
        // Act
        var parsed = ClientApp.ParseExportChanBackupOptions([s_channelHex, "--output", "out.backup"], out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(new ChannelId(Convert.FromHexString(s_channelHex)), parsed.ChannelId);
        Assert.Equal("out.backup", parsed.OutputPath);
    }

    [Theory]
    [InlineData("verifychanbackup")]
    [InlineData("verify-chan-backup")]
    [InlineData("restorechanbackup")]
    [InlineData("restore-chan-backup")]
    public void Given_VerifyChanBackupArguments_When_Validated_Then_AFileOrHexIsRequired(string command)
    {
        // Act / Assert
        Assert.Null(ClientApp.ValidateArguments(command, ["channel.backup"]));
        Assert.Null(ClientApp.ValidateArguments(command, ["--hex", "4e4c534342"]));
        Assert.NotNull(ClientApp.ValidateArguments(command, []));
        Assert.NotNull(ClientApp.ValidateArguments(command, ["--hex", "xyz"]));
        Assert.NotNull(ClientApp.ValidateArguments(command, ["--hex"]));
        Assert.NotNull(ClientApp.ValidateArguments(command, ["a", "b"]));
    }

    [Fact]
    public void Given_AVerification_When_Printed_Then_EachChannelShowsItsFundingKeysAndState()
    {
        // Arrange
        var output = new StringWriter();
        var response = new VerifyChanBackupIpcResponse
        {
            IsValid = false,
            Error = "The keys of 1 channel(s) do not derive from this node's key file (x).",
            CreatedAt = 1_790_000_000,
            Channels =
            [
                new ChanBackupChannelIpcInfo
                {
                    ChannelId = new ChannelId(new byte[32]),
                    RemoteNodeId = new Domain.Crypto.ValueObjects.CompactPubKey(
                        Convert.FromHexString("02" + new string('a', 64))),
                    Addresses = ["10.0.0.1:9735"],
                    FundingTxId = new string('b', 64),
                    FundingOutputIndex = 1,
                    CapacitySat = 1_000_000,
                    ShortChannelId = (700UL << 40) | (2UL << 16) | 1,
                    IsInitiator = true,
                    OptionAnchors = true,
                    KeysMatch = false,
                    LocalState = null
                }
            ]
        };

        // Act
        new VerifyChanBackupPrinter(output).Print(response);

        // Assert
        var text = output.ToString();
        Assert.Contains("Channel backup: INVALID", text);
        Assert.Contains("Created: 2026-09-21 ", text);
        Assert.Contains($"Funding: {new string('b', 64)}:1, 1000000 sat, 700x2x1", text);
        Assert.Contains("Type: anchors, we funded it", text);
        Assert.Contains("Keys: DO NOT MATCH", text);
        Assert.Contains("Local state: not in the database", text);
        Assert.Contains("@10.0.0.1:9735", text);
    }

    [Fact]
    public void Given_ATaprootChannelInTheBackup_When_VerificationAndExportPrinted_Then_ItIsNamedSimpleTaproot()
    {
        // Arrange (NL-877 T5)
        var verifyOutput = new StringWriter();
        var exportOutput = new StringWriter();
        var channelId = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        var verification = new VerifyChanBackupIpcResponse
        {
            IsValid = true,
            Channels =
            [
                new ChanBackupChannelIpcInfo
                {
                    ChannelId = channelId,
                    RemoteNodeId = new Domain.Crypto.ValueObjects.CompactPubKey(
                        Convert.FromHexString("02" + new string('a', 64))),
                    Addresses = [],
                    FundingTxId = new string('b', 64),
                    IsInitiator = false,
                    OptionAnchors = true,
                    OptionSimpleTaproot = true,
                    KeysMatch = true
                }
            ]
        };
        var export = new ExportChanBackupIpcResponse
        {
            Backup = [0x4e],
            ChannelIds = [channelId, new ChannelId(new byte[32])],
            SimpleTaprootChannelIds = [channelId]
        };

        // Act
        new VerifyChanBackupPrinter(verifyOutput).Print(verification);
        new ExportChanBackupPrinter(null, exportOutput).Print(export);

        // Assert
        Assert.Contains("Type: simple_taproot, the peer funded it", verifyOutput.ToString());
        Assert.Contains($"{channelId} (simple_taproot)", exportOutput.ToString());
        Assert.DoesNotContain($"{new ChannelId(new byte[32])} (simple_taproot)", exportOutput.ToString());
    }

    [Fact]
    public void Given_AnExportWithoutOutputFile_When_Printed_Then_TheBackupIsPrintedAsHex()
    {
        // Arrange
        var output = new StringWriter();
        var response = new ExportChanBackupIpcResponse
        {
            Backup = [0x4e, 0x4c],
            ChannelIds = [new ChannelId(new byte[32])],
            FilePath = null
        };

        // Act
        new ExportChanBackupPrinter(null, output).Print(response);

        // Assert
        var text = output.ToString();
        Assert.Contains("Channel backup: 1 channel(s), 2 bytes", text);
        Assert.Contains("Backup (hex): 4e4c", text);
        Assert.Contains("Node backup file: none", text);
    }

    [Fact]
    public void Given_ARestore_When_Printed_Then_EachChannelOutcomeAndEachPeerAreShown()
    {
        // Arrange
        var output = new StringWriter();
        var peer = new Domain.Crypto.ValueObjects.CompactPubKey(Enumerable.Repeat((byte)2, 33).ToArray());
        var response = new RestoreChanBackupIpcResponse
        {
            CreatedAt = 1_790_000_000,
            Channels =
            [
                new ChanRestoreChannelIpcInfo
                {
                    ChannelId = new ChannelId(Convert.FromHexString(s_channelHex)),
                    RemoteNodeId = peer,
                    CapacitySat = 1_000_000,
                    OptionAnchors = true,
                    Outcome = "Restore",
                    Detail = "recovery channel stored"
                },
                new ChanRestoreChannelIpcInfo
                {
                    ChannelId = new ChannelId(new byte[32]),
                    RemoteNodeId = peer,
                    CapacitySat = 500_000,
                    OptionAnchors = false,
                    Outcome = "AlreadyExists",
                    Detail = "already in the database (Open); left as it is"
                }
            ],
            Peers = [new ChanRestorePeerIpcInfo { NodeId = peer, Address = "10.0.0.1:9735", Connected = true }]
        };

        // Act
        new RestoreChanBackupPrinter(output).Print(response);

        // Assert
        var text = output.ToString();
        Assert.Contains("Channel backup restore: 1 of 2 channel(s) restored", text);
        Assert.Contains("Backup created: 2026-09-21 14:13:20Z", text);
        Assert.Contains($"  {s_channelHex}: Restore", text);
        Assert.Contains("1000000 sat, anchors", text);
        Assert.Contains("500000 sat, static_remotekey", text);
        Assert.Contains("AlreadyExists", text);
        Assert.Contains("@10.0.0.1:9735: connected, force close requested", text);
    }
}