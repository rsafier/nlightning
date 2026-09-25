using System.Security.Cryptography;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interpreters;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Validators;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Serialization.Onion;

/// <summary>
/// End to end on the <c>onion-error-test.json</c> route (5 hops): malformed conversion, channel_update embedding and
/// the origin's interpretation, with the real serializer and failure onion service.
/// </summary>
public class MalformedFailureConversionTests
{
    private readonly FailureMessageSerializer _serializer = new();
    private readonly FailureOnionService _failureOnionService;

    public MalformedFailureConversionTests()
    {
        _failureOnionService = new FailureOnionService(_serializer);
    }

    [Fact]
    public void Given_Hop4SendsMalformed_When_Hop3ConvertsAndOriginDecrypts_Then_ChannelFromHop3IsPenalized()
    {
        // Arrange: hop 4 could not parse the onion hop 3 sent and answered update_fail_malformed_htlc
        var vector = Bolt4Vectors.LoadOnionErrorTest();
        var route = vector.Hops.Select(h => new Secret(h.SharedSecret)).ToList();
        var sentOnion = RandomNumberGenerator.GetBytes(1366);
        var sentOnionSha256 = SHA256.HashData(sentOnion);
        const ushort malformedCode = (ushort)FailureCode.InvalidOnionHmac;

        // Act: hop 3 checks and converts it, hops 2..0 wrap it, the origin decrypts and interprets it
        var check = MalformedHtlcValidator.Validate(malformedCode, sentOnionSha256, sentOnionSha256);
        var packet = _failureOnionService.CreateErrorPacketFromMalformed(route[3], (FailureCode)malformedCode,
                                                                        sentOnionSha256);
        for (var i = 2; i >= 0; i--)
            packet = _failureOnionService.WrapErrorPacket(route[i], packet);

        var decrypted = _failureOnionService.DecryptErrorPacket(route, packet);
        var interpretation = FailureInterpreter.Interpret(decrypted, route.Count);

        // Assert
        Assert.Equal(MalformedHtlcCheckResult.Valid, check);
        Assert.NotNull(decrypted);
        Assert.Equal(3, decrypted.ErringHopIndex);
        Assert.Equal("c005" + Convert.ToHexStringLower(sentOnionSha256),
                     Convert.ToHexStringLower(decrypted.RawMessage.Span));
        Assert.Equal(sentOnionSha256, decrypted.Message!.Sha256OfOnion!.Value.ToArray());
        Assert.False(interpretation.IsFinalNode);
        Assert.False(interpretation.IsNodeFailure);
        Assert.True(interpretation.IsPermanent);
        Assert.Equal(4, interpretation.FailedChannelHopIndex);
        Assert.True(interpretation.ShouldRetry);
    }

    [Fact]
    public void Given_Hop3ConvertsMalformed_When_Framing_Then_BodyIsSha256OfOnionPaddedTo256()
    {
        // Arrange
        var sha256OfOnion = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var message = FailureMessage.FromMalformed(FailureCode.InvalidOnionKey, sha256OfOnion);

        // Act
        var body = _serializer.SerializeErrorPayload(message);

        // Assert: failure_len 34 || c006 || sha256 || pad_len 222 || zeros
        Assert.Equal("0022c006" + Convert.ToHexStringLower(sha256OfOnion) + "00de" + new string('0', 222 * 2),
                     Convert.ToHexStringLower(body));
    }

    [Fact]
    public void Given_Hop2ReturnsFeeInsufficientWithUpdate_When_OriginDecrypts_Then_UpdateIsRecovered()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionErrorTest();
        var route = vector.Hops.Select(h => new Secret(h.SharedSecret)).ToList();
        var channelUpdate = Enumerable.Range(0, FailureChannelUpdateFactory.MinPayloadLength)
                                      .Select(i => (byte)i).ToArray();
        var message = FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(1_000_000UL),
                                                     FailureChannelUpdateFactory.Encode(channelUpdate));

        // Act
        var packet = _failureOnionService.CreateErrorPacket(route[2], message);
        packet = _failureOnionService.WrapErrorPacket(route[1], packet);
        packet = _failureOnionService.WrapErrorPacket(route[0], packet);
        var interpretation =
            FailureInterpreter.Interpret(_failureOnionService.DecryptErrorPacket(route, packet), route.Count);

        // Assert
        Assert.Equal(2, interpretation.ErringHopIndex);
        Assert.Equal(FailureCode.FeeInsufficient, interpretation.Code);
        Assert.Equal(LightningMoney.MilliSatoshis(1_000_000UL), interpretation.Message!.HtlcAmount);
        Assert.Equal(3, interpretation.FailedChannelHopIndex);
        Assert.False(interpretation.IsPermanent);
        Assert.Equal(channelUpdate, interpretation.ChannelUpdate!.Value.ToArray());
    }
}