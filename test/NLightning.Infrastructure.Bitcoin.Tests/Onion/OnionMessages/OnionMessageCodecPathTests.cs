using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Infrastructure.Bitcoin.Onion.OnionMessages;

/// <summary>
/// NL-442: the packet builder and the unwrapper read and write <c>onionmsg_tlv</c> and <c>blinded_path</c> through the
/// Domain codecs only (<see cref="OnionMessageTlvsCodec"/>, <see cref="BlindedPathCodec"/>), the unwrapper adds the
/// curve check of a received <c>reply_path</c>, and the DI registration of the onion-message crypto.
/// </summary>
public class OnionMessageCodecPathTests
{
    private static readonly byte[] s_pathIdData = [0x06, 0x01, 0x07];

    private readonly OnionMessageTestKit _kit = new();

    [Fact]
    public void Given_AReplyPathOfEachIntroductionForm_When_WrittenByTheBuilder_Then_TheDomainCodecReadsItBack()
    {
        // Arrange
        var path = WireBlindedPath.FromBlindedPath(_kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..3]));
        var scidPath = path with
        {
            FirstNode = SciddirOrPubkey.FromShortChannelId(new ShortChannelId(1, 2, 3), 0)
        };

        foreach (var expected in new[] { path, scidPath })
        {
            // Act
            var payload = OnionMessagePacketBuilder.EncodeFinal(new byte[] { 9 }, expected,
                                                                OnionMessageContents.Single(65, new byte[] { 2 }));
            var decoded = OnionMessageTlvsCodec.TryDecode(payload, out var tlvs, out _);

            // Assert
            Assert.True(decoded);
            var actual = tlvs!.ReplyPath!;
            Assert.Equal(expected.FirstNode, actual.FirstNode);
            Assert.Equal(expected.FirstPathKey, actual.FirstPathKey);
            Assert.Equal(expected.Hops.Select(h => h.BlindedNodeId), actual.Hops.Select(h => h.BlindedNodeId));
            Assert.Equal(BlindedPathCodec.Encode(expected), BlindedPathCodec.Encode(actual));
            Assert.Equal(payload, OnionMessageTlvsCodec.Encode(tlvs));
        }
    }

    [Fact]
    public void Given_ANonFinalHop_When_Encoded_Then_OnlyEncryptedRecipientData()
    {
        // Act
        var payload = OnionMessagePacketBuilder.EncodeNonFinal(new byte[] { 1, 2, 3 });

        // Assert
        Assert.Equal(new byte[] { 0x04, 0x03, 1, 2, 3 }, payload);
    }

    [Theory]
    [InlineData(2ul)]
    [InlineData(4ul)]
    [InlineData(70ul)]
    public void Given_ContentsWithAReservedOrUnknownEvenType_When_EncodingTheFinalHop_Then_Refused(ulong type)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => OnionMessagePacketBuilder.EncodeFinal(
                                             new byte[] { 9 }, null, OnionMessageContents.Single(type, new byte[1])));
    }

    public static TheoryData<string, Func<byte[], byte[]>> MalformedReplyPaths => new()
    {
        { "unknown first byte", p => [0x04, .. p[1..]] },
        { "zero hops", p => [.. p[..67], 0] },
        { "trailing byte", p => [.. p, 0] },
        { "truncated", p => p[..^1] },
        { "first_path_key not a point", p => [.. p[..33], 0x02, .. Enumerable.Repeat((byte)0xFF, 32), .. p[66..]] },
        { "first_node_id not a point", p => [0x02, .. Enumerable.Repeat((byte)0xFF, 32), .. p[33..]] },
        { "truncated SCID form", _ => [0x00, 1, 2, 3] }
    };

    [Theory]
    [MemberData(nameof(MalformedReplyPaths))]
    public void Given_AFinalHopWithAMalformedReplyPath_When_Unwrapping_Then_IgnoredAsInvalidPayload(
        string name, Func<byte[], byte[]> mutate)
    {
        // Arrange
        var replyPath = mutate(BlindedPathCodec.Encode(
                                   WireBlindedPath.FromBlindedPath(
                                       _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1]))));
        var message = _kit.BuildRaw([s_pathIdData],
                                    (_, erd) => [.. OnionMessageTestKit.Tlv(OnionMessageConstants.ReplyPathType, replyPath),
                                                 .. OnionMessageTestKit.Tlv(
                                                     OnionMessageConstants.EncryptedRecipientDataType, erd)]);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        Assert.True(result.Status == OnionMessageUnwrapStatus.Ignored, name);
        Assert.Equal(OnionMessageIgnoreReason.InvalidPayload, result.IgnoreKind);
        Assert.NotNull(result.IgnoreReason);
    }

    [Theory]
    [InlineData("fd000100")] // non-canonical bigsize type
    [InlineData("01")]       // no length
    [InlineData("0105aa")]   // length past the end
    [InlineData("4600")]     // unknown even type 70
    public void Given_AMalformedFinalStream_When_Unwrapping_Then_IgnoredAsInvalidPayload(string hex)
    {
        // Arrange: the stream follows encrypted_recipient_data, so the types stay increasing only where intended
        var message = _kit.BuildRaw([s_pathIdData],
                                    (_, erd) => [.. OnionMessageTestKit.Tlv(
                                                     OnionMessageConstants.EncryptedRecipientDataType, erd),
                                                 .. Convert.FromHexString(hex)]);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Ignored, result.Status);
        Assert.Equal(OnionMessageIgnoreReason.InvalidPayload, result.IgnoreKind);
    }

    [Fact]
    public void Given_BitcoinInfrastructure_When_Resolving_Then_TheOnionMessageCryptoIsRegisteredAsSingletons()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ISecureKeyManager>(new EcdhOnlyKeyManager(_kit.NodeKeys[0]));
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();

        // Act
        var packetBuilder = provider.GetRequiredService<IOnionMessagePacketBuilder>();
        var pathBuilder = provider.GetRequiredService<IBlindedMessagePathBuilder>();
        var unwrapper = provider.GetRequiredService<IOnionMessageUnwrapper>();
        var path = pathBuilder.CreateMessagePath([_kit.NodeIds[0]], new byte[] { 1 });
        var message = packetBuilder.Build([], path, OnionMessageContents.Single(65, new byte[] { 2 }), null);

        // Assert
        Assert.Same(packetBuilder, provider.GetRequiredService<IOnionMessagePacketBuilder>());
        Assert.Same(unwrapper, provider.GetRequiredService<IOnionMessageUnwrapper>());
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, unwrapper.UnwrapAsLocalNode(message).Status);
    }
}