namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion.RouteBlinding;

/// <summary>
/// ONION M5: the <c>encrypted_data_tlv</c> reader's strictness and the blinded path round trip (the official vectors
/// are in <c>Integration.Tests/BOLT4/RouteBlindingVectorTests</c>).
/// </summary>
public class RouteBlindingServiceTests
{
    private static readonly PrivKey s_bobKey = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly PrivKey s_carolKey = Enumerable.Repeat((byte)0x43, 32).ToArray();
    private static readonly CompactPubKey s_bobId =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));
    private static readonly CompactPubKey s_carolId =
        new(Convert.FromHexString("027f31ebc5462c1fdce1b737ecff52d37d75dea43ce11c74d25aa297165faa2007"));

    private readonly RouteBlindingService _service = new(new Secp256K1Math());

    public static TheoryData<string, string> MalformedStreams => new()
    {
        { "unknown even type", "1000" },
        { "types out of order", "04210324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c020800000000000006c1" },
        { "repeated type", "01000100" },
        { "length past the end", "0105aa" },
        { "short_channel_id of 7 bytes", "020700000000000006" },
        { "next_node_id not a point", "0421" + "02" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" },
        { "fee_base_msat not minimal", "0a0700240000009600" },
        { "fee_base_msat too long", "0a0b002400000096000000000001" },
        { "payment_relay too short", "0a05002400000" + "0" },
        { "htlc_minimum_msat not minimal", "0c06000b69e5" + "0005" },
        { "non-canonical bigsize type", "fd0001" + "00" }
    };

    [Theory]
    [MemberData(nameof(MalformedStreams))]
    public void Given_MalformedEncryptedDataTlv_When_Decoded_Then_InvalidOnionBlinding(string reason, string hex)
    {
        // Act
        var exception = Assert.Throws<OnionException>(() => _service.DecodeRecipientData(Convert.FromHexString(hex)));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Given_UnknownOddRecord_When_Decoded_Then_KeptVerbatim()
    {
        // Act
        var data = _service.DecodeRecipientData(Convert.FromHexString("fd023103123456"));

        // Assert
        var (type, value) = Assert.Single(data.UnknownOddRecords);
        Assert.Equal(561UL, type);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, value.ToArray());
    }

    [Fact]
    public void Given_BlindedPath_When_EachHopUnblinds_Then_TheyReadTheirDataAndChainThePathKeys()
    {
        // Arrange
        var bobData = new BlindedRecipientData
        {
            ShortChannelId = new ShortChannelId(1, 2, 3),
            PaymentRelay = new BlindedPaymentRelay(40, 100, 1_000),
            PaymentConstraints = new BlindedPaymentConstraints(800_000, 1)
        };
        var carolData = new BlindedRecipientData { PathId = new byte[] { 0xde, 0xad } };
        var path = _service.CreateBlindedPath([s_bobId, s_carolId],
                                              [_service.EncodeRecipientData(bobData),
                                               _service.EncodeRecipientData(carolData)],
                                              Enumerable.Repeat((byte)0x05, 32).ToArray());

        // Act
        var bob = _service.Unblind(s_bobKey, path.FirstPathKey, path.Hops[0].EncryptedRecipientData);
        var carol = _service.Unblind(s_carolKey, bob.NextPathKey, path.Hops[1].EncryptedRecipientData);

        // Assert
        Assert.Equal(s_bobId, path.FirstNodeId);
        Assert.Equal(bobData.ShortChannelId, bob.RecipientData.ShortChannelId);
        Assert.Equal(bobData.PaymentRelay, bob.RecipientData.PaymentRelay);
        Assert.Equal(bobData.PaymentConstraints, bob.RecipientData.PaymentConstraints);
        Assert.Equal(new byte[] { 0xde, 0xad }, carol.RecipientData.PathId!.Value.ToArray());
        Assert.NotEqual(s_carolId, path.Hops[1].BlindedNodeId);
    }

    [Fact]
    public void Given_WrongNodeKey_When_Unblinding_Then_InvalidOnionBlinding()
    {
        // Arrange
        var path = _service.CreateBlindedPath([s_bobId], [[]], Enumerable.Repeat((byte)0x05, 32).ToArray());

        // Act
        var exception = Assert.Throws<OnionException>(() => _service.Unblind(
                                                          s_carolKey, path.FirstPathKey,
                                                          path.Hops[0].EncryptedRecipientData));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
    }

    [Fact]
    public void Given_PathKeyNotOnTheCurve_When_Unblinding_Then_InvalidOnionBlinding()
    {
        // Arrange (NL-077): 0x02 || ff..ff is not a point
        var notAPoint = new CompactPubKey(Convert.FromHexString("02" + new string('f', 64)));

        // Act
        var exception = Assert.Throws<OnionException>(() => _service.Unblind(s_bobKey, notAPoint, new byte[32]));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
    }

    [Fact]
    public void Given_DataShorterThanTheTag_When_Unblinding_Then_InvalidOnionBlinding()
    {
        // Act
        var exception = Assert.Throws<OnionException>(() => _service.Unblind(s_bobKey, s_carolId, new byte[15]));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
    }

    [Fact]
    public void Given_NoKeyManager_When_UnblindingAsLocalNodeWithoutSecret_Then_InvalidOperation()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _service.UnblindAsLocalNode(s_carolId, new byte[16]));
    }

    [Fact]
    public void Given_MismatchedLists_When_CreatingAPath_Then_ArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.CreateBlindedPath([s_bobId], [], s_bobKey));
        Assert.Throws<ArgumentException>(() => _service.CreateBlindedPath([], [], s_bobKey));
    }
}