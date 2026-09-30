namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>listpeerstorage</c> (ClientCommand 32, NL-432): argument parsing and the printed result.
/// </summary>
public class PeerStorageCommandTests
{
    private const string NodeIdHex = "02" + "1111111111111111111111111111111111111111111111111111111111111111";

    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { "--blob" }, false, true)]
    [InlineData(new[] { NodeIdHex }, true, false)]
    [InlineData(new[] { "--blob", NodeIdHex }, true, true)]
    public void Given_ValidArguments_When_Parsed_Then_NodeIdAndBlobFlag(string[] args, bool hasNodeId,
                                                                         bool includeBlob)
    {
        // Act
        var parsed = PeerStorageCommands.ParseListPeerStorageOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(hasNodeId, parsed!.Value.NodeId is not null);
        Assert.Equal(includeBlob, parsed.Value.IncludeBlob);
        Assert.Null(ClientApp.ValidateArguments("listpeerstorage", args));
        Assert.Null(ClientApp.ValidateArguments("list-peer-storage", args));
    }

    [Theory]
    [InlineData("nothex")]
    [InlineData("04" + "11111111111111111111111111111111111111111111111111111111111111111")]
    public void Given_ABadNodeId_When_Validated_Then_UsageError(string argument)
    {
        // Act
        var error = ClientApp.ValidateArguments("listpeerstorage", [argument]);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(PeerStorageCommands.ListPeerStorageUsage, error);
    }

    [Fact]
    public void Given_TwoNodeIds_When_Validated_Then_UsageError()
    {
        // Act
        var error = ClientApp.ValidateArguments("listpeerstorage", [NodeIdHex, NodeIdHex]);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Unexpected argument", error);
    }

    [Fact]
    public void Given_ALostChannel_When_Printed_Then_ItIsMarkedForRestoreWithTheHint()
    {
        // Arrange
        var peer = new CompactPubKey(Convert.FromHexString(NodeIdHex));
        var lost = new ChannelId(Enumerable.Repeat((byte)0xAB, 32).ToArray());
        var known = new ChannelId(Enumerable.Repeat((byte)0xCD, 32).ToArray());
        var response = new ListPeerStorageIpcResponse
        {
            Retrievals =
            [
                new PeerStorageRetrievalIpcInfo
                {
                    PeerNodeId = peer,
                    ReceivedAt = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero),
                    BlobLength = 65531,
                    IsOurs = true,
                    BackupCreatedAt = new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero),
                    MatchesLastSent = null,
                    Persisted = false,
                    Channels =
                    [
                        new PeerStorageChannelIpcInfo
                        {
                            ChannelId = lost, PeerNodeId = peer, UnknownWhenReceived = true, KnownNow = false
                        },
                        new PeerStorageChannelIpcInfo
                        {
                            ChannelId = known, PeerNodeId = peer, UnknownWhenReceived = true, KnownNow = true
                        }
                    ],
                    Blob = [0xDE, 0xAD]
                }
            ],
            StoredBlobs =
            [
                new StoredPeerBlobIpcInfo
                {
                    PeerNodeId = peer, BlobLength = 12,
                    UpdatedAt = new DateTimeOffset(2026, 9, 27, 11, 0, 0, TimeSpan.Zero)
                }
            ],
            Refusals =
            [
                new PeerRefusalIpcInfo
                {
                    PeerNodeId = peer, Count = 2, AcceptedLimitBytes = 1024, LastRefusedBlobLength = 65531,
                    LastRefusalAt = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero)
                }
            ],
            BackupsHeldForDataLoss = true
        };
        using var output = new StringWriter();

        // Act
        new ListPeerStoragePrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.Contains("Backups handed back by peers (1)", printed);
        Assert.Contains("go to no peer until the restart", printed);
        Assert.Contains("2026-09-27 10:00:00Z (not stored yet)", printed);
        Assert.Contains("65531 bytes, our backup", printed);
        Assert.Contains("none sent by that process", printed);
        Assert.Contains($"Channel {lost} with {peer}: UNKNOWN (restore it)", printed);
        Assert.Contains($"Channel {known} with {peer}: restored", printed);
        Assert.Contains("Blob hex:        dead", printed);
        Assert.Contains("1 channel(s) named by a peer's copy are unknown", printed);
        Assert.Contains($"{peer}: 12 bytes, updated 2026-09-27 11:00:00Z", printed);
        Assert.Contains("Peers that refused our backup for its size (1)", printed);
        Assert.Contains($"{peer}: 2 refusal(s), last 2026-09-27 12:00:00Z, takes at most 1024 bytes (refused 65531)",
                        printed);
        Assert.Contains("it keeps nothing of ours until a blob within 1024 bytes reaches it", printed);
    }
}