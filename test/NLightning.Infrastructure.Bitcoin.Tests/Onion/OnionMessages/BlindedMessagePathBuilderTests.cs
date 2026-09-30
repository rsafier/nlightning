namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Onion.OnionMessages;

/// <summary>
/// OM1-T1: message-path creation, equal-length padding (OM-S-08) and the creator's rules (OM-S-04).
/// </summary>
public class BlindedMessagePathBuilderTests
{
    private readonly OnionMessageTestKit _kit = new();

    [Theory]
    [InlineData(0, -1)]
    [InlineData(2, 0)]
    [InlineData(7, 5)]
    [InlineData(254, 252)]
    [InlineData(257, 253)]
    [InlineData(1000, 996)]
    [InlineData(65539, 65535)]
    [InlineData(65542, 65536)]
    public void Given_ALengthDifference_When_ComputingThePadding_Then_TheRecordFillsItExactly(int difference,
        int expected)
    {
        // Act
        var valueLength = BlindedMessagePathBuilder.GetPaddingValueLength(difference);

        // Assert
        Assert.Equal(expected, valueLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65540)]
    [InlineData(65541)]
    public void Given_ADifferenceNoRecordCanFill_When_ComputingThePadding_Then_Null(int difference)
    {
        // Act
        var valueLength = BlindedMessagePathBuilder.GetPaddingValueLength(difference);

        // Assert
        Assert.Null(valueLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(300)]
    public void Given_HopsOfDifferentSizes_When_Encoding_Then_AllHaveTheSameLengthAndDecode(int pathIdLength)
    {
        // Arrange: a path_id of every interesting size against a next_node_id hop (35 bytes)
        var data = new List<BlindedRecipientData>
        {
            new() { NextNodeId = _kit.NodeIds[1] },
            new() { NextNodeId = _kit.NodeIds[2], ShortChannelId = new ShortChannelId(1, 2, 3) },
            new() { PathId = new byte[pathIdLength] }
        };

        // Act
        var encoded = _kit.PathBuilder.EncodePaddedHops(data);

        // Assert
        Assert.Single(encoded.Select(e => e.Length).Distinct());
        Assert.Equal(encoded.Max(e => e.Length),
                     data.Select(d => _kit.RouteBlinding.EncodeRecipientData(d).Length).Max() + ExtraFor(data));
        for (var i = 0; i < data.Count; i++)
        {
            var decoded = _kit.RouteBlinding.DecodeRecipientData(encoded[i]);
            Assert.Equal(data[i].NextNodeId, decoded.NextNodeId);
            Assert.Equal(data[i].PathId?.ToArray(), decoded.PathId?.ToArray());
            Assert.True(decoded.Padding is null || decoded.Padding.Value.Span.IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [Fact]
    public void Given_CallerPadding_When_Encoding_Then_TheBuilderReplacesIt()
    {
        // Arrange
        var data = new List<BlindedRecipientData>
        {
            new() { NextNodeId = _kit.NodeIds[1], Padding = new byte[40] },
            new() { PathId = new byte[3] }
        };

        // Act
        var encoded = _kit.PathBuilder.EncodePaddedHops(data);

        // Assert: the first hop is the longest once its padding is dropped, so it carries none
        Assert.Null(_kit.RouteBlinding.DecodeRecipientData(encoded[0]).Padding);
        Assert.Equal(encoded[0].Length, encoded[1].Length);
    }

    [Fact]
    public void Given_PaymentRelay_When_CreatingAMessagePath_Then_Refused()
    {
        // Arrange (OM-S-04: the creator MUST NOT include payment_relay or payment_constraints)
        var data = new List<BlindedRecipientData>
        {
            new() { PathId = new byte[1], PaymentRelay = new BlindedPaymentRelay(40, 100, 1000) }
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PathBuilder.CreatePath([_kit.NodeIds[0]], data));
    }

    [Fact]
    public void Given_PaymentConstraints_When_CreatingAMessagePath_Then_Refused()
    {
        // Arrange
        var data = new List<BlindedRecipientData>
        {
            new() { PathId = new byte[1], PaymentConstraints = new BlindedPaymentConstraints(100, 1) }
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PathBuilder.CreatePath([_kit.NodeIds[0]], data));
    }

    [Fact]
    public void Given_ANonFinalHopWithoutNextHop_When_CreatingAPath_Then_Refused()
    {
        // Arrange
        var data = new List<BlindedRecipientData> { new(), new() };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PathBuilder.CreatePath(_kit.NodeIds[..2], data));
    }

    [Fact]
    public void Given_ANonFinalHopWithPathId_When_CreatingAPath_Then_Refused()
    {
        // Arrange
        var data = new List<BlindedRecipientData>
        {
            new() { NextNodeId = _kit.NodeIds[1], PathId = new byte[1] },
            new()
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PathBuilder.CreatePath(_kit.NodeIds[..2], data));
    }

    [Fact]
    public void Given_CountsThatDiffer_When_CreatingAPath_Then_Refused()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PathBuilder.CreatePath(_kit.NodeIds[..2], [new()]));
        Assert.Throws<ArgumentException>(() => _kit.PathBuilder.CreatePath([], []));
        Assert.Throws<ArgumentException>(() => _kit.PathBuilder.CreateMessagePath([]));
    }

    [Fact]
    public void Given_NodeIdsAndAPathId_When_CreatingAMessagePath_Then_EachHopNamesTheNextAndTheLastHasThePathId()
    {
        // Arrange
        var pathId = new byte[] { 1, 2, 3 };

        // Act
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds, pathId);

        // Assert
        Assert.Equal(_kit.NodeIds[0], path.FirstNodeId);
        Assert.Single(path.Hops.Select(h => h.EncryptedRecipientData.Length).Distinct());
        var pathKey = path.FirstPathKey;
        for (var i = 0; i < _kit.NodeIds.Length; i++)
        {
            var unblinded = _kit.RouteBlinding.Unblind(_kit.NodeKeys[i], pathKey, path.Hops[i].EncryptedRecipientData);
            if (i < _kit.NodeIds.Length - 1)
            {
                Assert.Equal(_kit.NodeIds[i + 1], unblinded.RecipientData.NextNodeId);
                Assert.Null(unblinded.RecipientData.PathId);
            }
            else
            {
                Assert.Null(unblinded.RecipientData.NextNodeId);
                Assert.Equal(pathId, unblinded.RecipientData.PathId!.Value.ToArray());
            }

            pathKey = unblinded.NextPathKey;
        }
    }

    [Fact]
    public void Given_DummyHops_When_CreatingAMessagePath_Then_TheyRelayToTheRecipientAndCarryThePadding()
    {
        // Arrange (NL-525): the recipient's dummy hops obscure the path length
        var pathId = new byte[] { 7, 8 };

        // Act
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..2], pathId, dummyHops: 2);

        // Assert: peer, us, us, us — every padded alike, the dummies relay to the recipient, the last has the path_id
        Assert.Equal(4, path.Hops.Count);
        Assert.Single(path.Hops.Select(h => h.EncryptedRecipientData.Length).Distinct());
        var pathKey = path.FirstPathKey;
        var keys = new[] { _kit.NodeKeys[0], _kit.NodeKeys[1], _kit.NodeKeys[1], _kit.NodeKeys[1] };
        for (var i = 0; i < path.Hops.Count; i++)
        {
            var unblinded = _kit.RouteBlinding.Unblind(keys[i], pathKey, path.Hops[i].EncryptedRecipientData);
            if (i < path.Hops.Count - 1)
            {
                Assert.Equal(_kit.NodeIds[1], unblinded.RecipientData.NextNodeId);
                Assert.Null(unblinded.RecipientData.PathId);
            }
            else
            {
                Assert.Null(unblinded.RecipientData.NextNodeId);
                Assert.Equal(pathId, unblinded.RecipientData.PathId!.Value.ToArray());
            }

            pathKey = unblinded.NextPathKey;
        }
    }

    [Fact]
    public void Given_NegativeDummyHops_When_CreatingAMessagePath_Then_Refused()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1], dummyHops: -1));
    }

    [Fact]
    public void Given_NoSessionKey_When_CreatingTwoPaths_Then_EachGetsAFreshOne()
    {
        // Act
        var first = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..2]);
        var second = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..2]);

        // Assert
        Assert.NotEqual(first.FirstPathKey, second.FirstPathKey);
        Assert.NotEqual(first.Hops[0].BlindedNodeId, second.Hops[0].BlindedNodeId);
    }

    /// <summary>
    /// How far the target grows past the longest hop so every difference can be padded (only a difference of 1
    /// needs it here).
    /// </summary>
    private int ExtraFor(IReadOnlyList<BlindedRecipientData> data)
    {
        var lengths = data.Select(d => _kit.RouteBlinding.EncodeRecipientData(d).Length).ToList();
        var extra = 0;
        while (lengths.Any(l => BlindedMessagePathBuilder.GetPaddingValueLength(lengths.Max() + extra - l) is null))
            extra++;
        return extra;
    }
}