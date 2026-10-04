using System.Buffers.Binary;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Protocol.ValueObjects;

public class ChannelBackupCodecTests
{
    [Fact]
    public void Given_ASnapshotWithAnchorsAndLegacyChannels_When_EncodedAndDecoded_Then_EveryFieldRoundTrips()
    {
        // Arrange
        var snapshot = BackupTestData.SampleSnapshot();

        // Act
        var decoded = ChannelBackupCodec.Decode(ChannelBackupCodec.Encode(snapshot));

        // Assert
        Assert.Equal(snapshot.ChainHash, decoded.ChainHash);
        Assert.Equal(snapshot.NodeId, decoded.NodeId);
        Assert.Equal(snapshot.CreatedAt, decoded.CreatedAt);
        Assert.Equal(2, decoded.Channels.Count);
        for (var i = 0; i < snapshot.Channels.Count; i++)
            BackupTestData.AssertSameEntry(snapshot.Channels[i], decoded.Channels[i]);

        Assert.True(decoded.Channels[0].OptionAnchorOutputs);
        Assert.Equal(new ShortChannelId(700_123, 42, 1), decoded.Channels[0].ShortChannelId);
        Assert.False(decoded.Channels[1].OptionAnchorOutputs);
        Assert.Null(decoded.Channels[1].ShortChannelId);
        Assert.Equal(FeatureSupport.Optional, decoded.Channels[1].UseScidAlias);
        Assert.Equal(2, decoded.Channels[1].Addresses.Count);
    }

    [Fact]
    public void Given_ASimpleTaprootChannel_When_EncodedAndDecoded_Then_VersionTwoAndTheTaprootFlagRoundTrips()
    {
        // Arrange (NL-877 T5)
        var data = new BackupTestData();
        var taproot = data.AddChannel(3, scid: new ShortChannelId(700_200, 7, 0), simpleTaproot: true);
        var anchors = data.AddChannel(4, anchors: true);
        var snapshot = BackupTestData.SampleSnapshot() with
        {
            Channels =
            [
                ChannelBackupService.CreateEntry(taproot, data.Peers[0]),
                ChannelBackupService.CreateEntry(anchors, data.Peers[1])
            ]
        };

        // Act
        var encoded = ChannelBackupCodec.Encode(snapshot);
        var decoded = ChannelBackupCodec.Decode(encoded);

        // Assert: version 2 (a reader that predates taproot refuses it rather than restore an anchors channel)
        Assert.Equal(ChannelBackupCodec.TaprootVersion, encoded[0]);
        Assert.True(decoded.Channels[0].OptionSimpleTaproot);
        Assert.True(decoded.Channels[0].OptionAnchorOutputs);
        Assert.False(decoded.Channels[1].OptionSimpleTaproot);
        for (var i = 0; i < snapshot.Channels.Count; i++)
            BackupTestData.AssertSameEntry(snapshot.Channels[i], decoded.Channels[i]);
    }

    [Fact]
    public void Given_ABackupWithoutTaprootChannels_When_Encoded_Then_ItStaysVersionOneAndFlagBit4IsIgnoredThere()
    {
        // Arrange: older nodes keep reading backups without taproot channels
        var encoded = ChannelBackupCodec.Encode(BackupTestData.SampleSnapshot());
        const int firstFlagsOffset = 1 + 32 + 33 + 8 + 2 + 2 + 32 + 33 + 32 + 2 + 8 + 4 + 8;

        // Act: a version 1 record whose unknown flag bit 4 is set (an old writer never sets it)
        encoded[firstFlagsOffset] |= 16;
        var decoded = ChannelBackupCodec.Decode(encoded);

        // Assert
        Assert.Equal(ChannelBackupCodec.Version, encoded[0]);
        Assert.False(decoded.Channels[0].OptionSimpleTaproot);
        Assert.True(decoded.Channels[0].OptionAnchorOutputs);
    }

    [Fact]
    public void Given_AnEmptySnapshot_When_EncodedAndDecoded_Then_ItHasNoChannels()
    {
        // Arrange
        var snapshot = BackupTestData.SampleSnapshot() with { Channels = [] };

        // Act
        var decoded = ChannelBackupCodec.Decode(ChannelBackupCodec.Encode(snapshot));

        // Assert
        Assert.Empty(decoded.Channels);
        Assert.Equal(BitcoinNetwork.Regtest.ChainHash, decoded.ChainHash);
    }

    [Fact]
    public void Given_AnyTruncation_When_Decoded_Then_ItIsRefusedAsMalformed()
    {
        // Arrange
        var encoded = ChannelBackupCodec.Encode(BackupTestData.SampleSnapshot());

        // Act / Assert
        for (var length = 0; length < encoded.Length; length++)
        {
            var truncated = encoded[..length];
            Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCodec.Decode(truncated));
        }
    }

    [Fact]
    public void Given_BytesAfterTheLastChannel_When_Decoded_Then_ItIsRefused()
    {
        // Arrange
        var encoded = ChannelBackupCodec.Encode(BackupTestData.SampleSnapshot()).Concat(new byte[] { 0 }).ToArray();

        // Act / Assert
        var e = Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCodec.Decode(encoded));
        Assert.Contains("after the last channel", e.Message);
    }

    [Fact]
    public void Given_AnUnknownVersion_When_Decoded_Then_ItIsRefused()
    {
        // Arrange
        var encoded = ChannelBackupCodec.Encode(BackupTestData.SampleSnapshot());
        encoded[0] = 3;

        // Act / Assert
        var e = Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCodec.Decode(encoded));
        Assert.Contains("version 3", e.Message);
    }

    [Fact]
    public void Given_ARecordWithExtraTrailingBytes_When_Decoded_Then_TheExtraBytesAreSkipped()
    {
        // Arrange: a later minor revision appends a field to the record (the record length covers it)
        var snapshot = BackupTestData.SampleSnapshot() with
        {
            Channels = [BackupTestData.SampleSnapshot().Channels[0]]
        };
        var encoded = ChannelBackupCodec.Encode(snapshot);
        const int recordLengthOffset = 1 + 32 + 33 + 8 + 2;
        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(recordLengthOffset));
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(recordLengthOffset), (ushort)(recordLength + 3));
        var extended = encoded.Concat(new byte[] { 0xAA, 0xBB, 0xCC }).ToArray();

        // Act
        var decoded = ChannelBackupCodec.Decode(extended);

        // Assert
        BackupTestData.AssertSameEntry(snapshot.Channels[0], Assert.Single(decoded.Channels));
    }

    [Fact]
    public void Given_TheSameChannelTwice_When_Decoded_Then_ItIsRefused()
    {
        // Arrange
        var channel = BackupTestData.SampleSnapshot().Channels[0];
        var encoded = ChannelBackupCodec.Encode(BackupTestData.SampleSnapshot() with { Channels = [channel, channel] });

        // Act / Assert
        var e = Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCodec.Decode(encoded));
        Assert.Contains("twice", e.Message);
    }

    [Fact]
    public void Given_AnUncompressedNodeId_When_Decoded_Then_ItIsRefused()
    {
        // Arrange
        var encoded = ChannelBackupCodec.Encode(BackupTestData.SampleSnapshot());
        encoded[1 + 32] = 0x04;

        // Act / Assert
        Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCodec.Decode(encoded));
    }

    [Fact]
    public void Given_AnAddressLongerThan255Bytes_When_Encoded_Then_ItIsRefused()
    {
        // Arrange
        var snapshot = BackupTestData.SampleSnapshot();
        var channel = snapshot.Channels[0] with
        {
            Addresses = [new Application.Channels.Backup.Models.ChannelBackupAddress("DNS", new string('a', 256), 1)]
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelBackupCodec.Encode(snapshot with { Channels = [channel] }));
    }
}