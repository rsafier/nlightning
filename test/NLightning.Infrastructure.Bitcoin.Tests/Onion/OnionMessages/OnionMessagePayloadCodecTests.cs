using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Interfaces;
using Infrastructure.Bitcoin.Onion.OnionMessages;

/// <summary>
/// The internal <c>onionmsg_tlv</c> / <c>blinded_path</c> codec of the builder and the unwrapper, and the DI
/// registration of the onion-message crypto.
/// </summary>
public class OnionMessagePayloadCodecTests
{
    private readonly OnionMessageTestKit _kit = new();

    [Fact]
    public void Given_ABlindedPathOfEachIntroductionForm_When_RoundTripping_Then_Equal()
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
            var encoded = OnionMessagePayloadCodec.EncodeBlindedPath(expected);
            var decoded = OnionMessagePayloadCodec.TryDecodeBlindedPath(encoded, out var actual);

            // Assert
            Assert.True(decoded);
            Assert.Equal(expected.FirstNode, actual!.FirstNode);
            Assert.Equal(expected.FirstPathKey, actual.FirstPathKey);
            Assert.Equal(expected.Hops.Select(h => h.BlindedNodeId), actual.Hops.Select(h => h.BlindedNodeId));
            Assert.Equal(expected.Hops.Select(h => h.EncryptedRecipientData.ToArray()),
                         actual.Hops.Select(h => h.EncryptedRecipientData.ToArray()));
            Assert.Equal(encoded, OnionMessagePayloadCodec.EncodeBlindedPath(actual));
        }
    }

    public static TheoryData<string, Func<byte[], byte[]>> MalformedPaths => new()
    {
        { "empty", _ => [] },
        { "unknown first byte", p => [0x04, .. p[1..]] },
        { "zero hops", p => [.. p[..67], 0] },
        { "trailing byte", p => [.. p, 0] },
        { "truncated", p => p[..^1] },
        { "first_path_key not a point", p => [.. p[..33], 0x02, .. Enumerable.Repeat((byte)0xFF, 32), .. p[66..]] },
        { "truncated SCID form", _ => [0x00, 1, 2, 3] }
    };

    [Theory]
    [MemberData(nameof(MalformedPaths))]
    public void Given_AMalformedBlindedPath_When_Decoding_Then_False(string name, Func<byte[], byte[]> mutate)
    {
        // Arrange
        var encoded = OnionMessagePayloadCodec.EncodeBlindedPath(
            WireBlindedPath.FromBlindedPath(_kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1])));

        // Act
        var decoded = OnionMessagePayloadCodec.TryDecodeBlindedPath(mutate(encoded), out var path);

        // Assert
        Assert.False(decoded, name);
        Assert.Null(path);
    }

    [Theory]
    [InlineData("fd000100")] // non-canonical bigsize type
    [InlineData("01")]       // no length
    [InlineData("0105aa")]   // length past the end
    public void Given_AMalformedStream_When_Decoding_Then_False(string hex)
    {
        // Act
        var decoded = OnionMessagePayloadCodec.TryDecode(Convert.FromHexString(hex), out var tlvs, out var reason);

        // Assert
        Assert.False(decoded);
        Assert.Null(tlvs);
        Assert.NotNull(reason);
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