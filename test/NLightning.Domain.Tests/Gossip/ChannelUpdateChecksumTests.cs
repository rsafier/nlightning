using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.Gossip;

using Domain.Gossip.Queries;
using Domain.Protocol.Payloads;

/// <summary>
/// The <c>checksums_tlv</c> checksum (BOLT 7 G3-T4): CRC32C (RFC 3720) of a <c>channel_update</c> without its signature
/// and timestamp.
/// </summary>
public class ChannelUpdateChecksumTests
{
    [Theory]
    [InlineData("313233343536373839", 0xE3069283u)] // "123456789", the CRC-32C check value
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", 0x8A9136AAu)] // RFC 3720 B.4
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", 0x62A8AB43u)] // RFC 3720 B.4
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", 0x46DD794Eu)] // RFC 3720 B.4
    [InlineData("", 0u)]
    public void Given_RfcVectors_When_Crc32CIsComputed_Then_TheValuesMatch(string hex, uint expected)
    {
        // Act / Assert
        Assert.Equal(expected, ChannelUpdateChecksum.Crc32C(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData(0, 0xAF5CB2FBu)]
    [InlineData(1, 0xF068B7E9u)]
    public void Given_CapturedClnUpdates_When_TheChecksumIsComputed_Then_ItMatchesTheReference(int index,
        uint expected)
    {
        // Arrange: the CLN v26.06.8 captures (plan G0-T5); expected values from a bitwise CRC32C over
        // payload[64..104] || payload[108..] (chain_hash, short_channel_id, then everything after the timestamp)
        var update = Bolt7Vectors.Cln.Where(v => v.Type == 258).ElementAt(index);

        // Act / Assert
        Assert.Equal(expected, ChannelUpdateChecksum.Compute(update.Payload));
    }

    [Fact]
    public void Given_EveryCapturedUpdate_When_TheChecksumIsComputed_Then_ItEqualsABitwiseReference()
    {
        foreach (var update in Bolt7Vectors.All.Where(v => v.Type == 258))
        {
            // Arrange
            var payload = update.Payload;
            var covered = payload[64..104].Concat(payload[108..]).ToArray();

            // Act / Assert
            Assert.Equal(BitwiseCrc32C(covered), ChannelUpdateChecksum.Compute(payload));
        }
    }

    [Fact]
    public void Given_AnUpdate_When_OnlyTheSignatureOrTimestampChanges_Then_TheChecksumStaysAndAFeeChangeMovesIt()
    {
        // Arrange
        var update = ChannelUpdatePayload.Parse(Bolt7Vectors.Cln.First(v => v.Type == 258).Payload);
        var resigned = update.WithSignature(ChannelUpdatePayload.EmptySignature);
        var later = new ChannelUpdatePayload(update.Signature, update.ChainHash, update.ShortChannelId,
                                             update.Timestamp + 100, update.MessageFlags, update.ChannelFlags,
                                             update.CltvExpiryDelta, update.HtlcMinimumMsat, update.FeeBaseMsat,
                                             update.FeeProportionalMillionths, update.HtlcMaximumMsat);
        var otherFee = new ChannelUpdatePayload(update.Signature, update.ChainHash, update.ShortChannelId,
                                                update.Timestamp, update.MessageFlags, update.ChannelFlags,
                                                update.CltvExpiryDelta, update.HtlcMinimumMsat,
                                                update.FeeBaseMsat + 1, update.FeeProportionalMillionths,
                                                update.HtlcMaximumMsat);
        var checksum = ChannelUpdateChecksum.Compute(update.GetBytes());

        // Act / Assert
        Assert.Equal(checksum, ChannelUpdateChecksum.Compute(resigned.GetBytes()));
        Assert.Equal(checksum, ChannelUpdateChecksum.Compute(later.GetBytes()));
        Assert.NotEqual(checksum, ChannelUpdateChecksum.Compute(otherFee.GetBytes()));
    }

    [Fact]
    public void Given_AShortPayload_When_TheChecksumIsComputed_Then_ItThrows()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelUpdateChecksum.Compute(new byte[ChannelUpdatePayload.MinLength - 1]));
    }

    private static uint BitwiseCrc32C(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
        }

        return ~crc;
    }
}