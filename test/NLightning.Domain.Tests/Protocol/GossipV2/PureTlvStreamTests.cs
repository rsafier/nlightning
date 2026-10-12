using System.Security.Cryptography;
using System.Text;

namespace NLightning.Domain.Tests.Protocol.GossipV2;

using Domain.Protocol.GossipV2;

/// <summary>
/// The pure TLV stream of taproot gossip (BOLTs PR #1059, NL-878): the signed ranges, BOLT 1's stream rules and the
/// BOLT 12-style MsgHash.
/// </summary>
public class PureTlvStreamTests
{
    [Theory]
    [InlineData(0UL, true)]
    [InlineData(239UL, true)]
    [InlineData(240UL, false)]
    [InlineData(999_999_999UL, false)]
    [InlineData(1_000_000_000UL, true)]
    [InlineData(2_999_999_999UL, true)]
    [InlineData(3_000_000_000UL, false)]
    public void Given_AType_When_IsSignedType_Then_TheDraftRangesApply(ulong type, bool signed)
    {
        // Act / Assert
        Assert.Equal(signed, PureTlvStream.IsSignedType(type));
    }

    [Fact]
    public void Given_AStream_When_GetSignedBytes_Then_OnlySignedRecordsInOrderExactlyAsOnTheWire()
    {
        // Arrange: 1, 240 (signature), 241, 1,000,000,001 (bigsize fe 3b9aca01)
        var bytes = Convert.FromHexString("0101aa" + "f001bb" + "f101cc" + "fe3b9aca0101dd");
        var stream = PureTlvStream.Parse(bytes, new HashSet<ulong> { 240 });

        // Act
        var signed = stream.GetSignedBytes();

        // Assert
        Assert.Equal(Convert.FromHexString("0101aa" + "fe3b9aca0101dd"), signed);
        Assert.Equal(bytes, stream.GetBytes());
    }

    [Theory]
    [InlineData("0201aa")] // unknown even type
    [InlineData("0301aa0101bb")] // decreasing
    [InlineData("0301aa0301bb")] // duplicate
    [InlineData("0305aa")] // length past the end
    [InlineData("fd000101aa")] // non-canonical bigsize
    public void Given_AStreamBreakingBolt1_When_Parsed_Then_FormatException(string hex)
    {
        // Act / Assert
        Assert.Throws<FormatException>(() => PureTlvStream.Parse(Convert.FromHexString(hex), new HashSet<ulong>()));
    }

    [Fact]
    public void Given_AKnownEvenType_When_Parsed_Then_ItIsAccepted()
    {
        // Act
        var stream = PureTlvStream.Parse(Convert.FromHexString("0201aa"), new HashSet<ulong> { 2 });

        // Assert
        Assert.Equal(new byte[] { 0xaa }, stream.Get(2)!.Value.ToArray());
    }

    [Fact]
    public void Given_AMessage_When_MsgHash_Then_ItIsTheTaggedHashOfLightningNameField()
    {
        // Arrange
        var m = new byte[] { 1, 2, 3 };
        var tag = SHA256.HashData(Encoding.UTF8.GetBytes("lightningchannel_update_2signature"));
        var expected = SHA256.HashData([.. tag, .. tag, .. m]);

        // Act
        var hash = GossipV2MsgHash.Compute("channel_update_2", "signature", m);

        // Assert
        Assert.Equal(expected, (byte[])hash);
    }

    [Fact]
    public void Given_AStream_When_WithARecord_Then_ItIsInsertedInOrderOrReplaced()
    {
        // Arrange
        var stream = new PureTlvStream([new PureTlvRecord(1, [1]), new PureTlvRecord(5, [5])]);

        // Act
        var inserted = stream.With(new PureTlvRecord(3, [3]));
        var replaced = inserted.With(new PureTlvRecord(5, [6]));

        // Assert
        Assert.Equal([1UL, 3UL, 5UL], inserted.Records.Select(r => r.Type));
        Assert.Equal(new byte[] { 6 }, replaced.Get(5)!.Value.ToArray());
        Assert.Throws<ArgumentException>(() => new PureTlvStream([new PureTlvRecord(2, []), new PureTlvRecord(1, [])]));
    }
}