using Microsoft.Extensions.DependencyInjection;
using NBitcoin.Secp256k1;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT7;

using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// Liquidity ads (NL-771, plan LA2): Eclair 0.14.3's <c>node_announcement</c> with <c>option_will_fund</c>
/// (LightningMessageCodecsSpec "encode/decode liquidity ads") parses through the node's own message serializer,
/// re-serializes byte-identically, its signature (which covers the TLV stream after the addresses) verifies against
/// Eclair's node key, and <see cref="NodeAnnouncementRates"/> reads the seller's rates from it.
/// </summary>
public class LiquidityAdsNodeAnnouncementVectorTests
{
    private readonly IMessageSerializer _serializer;

    public LiquidityAdsNodeAnnouncementVectorTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializationInfrastructureServices();
        _serializer = services.BuildServiceProvider().GetRequiredService<IMessageSerializer>();
    }

    [Fact]
    public async Task Given_EclairsNodeAnnouncement_When_RoundTripped_Then_ItIsByteExact()
    {
        // Arrange
        var wire = Convert.FromHexString(LiquidityAdsEclairVectors.NodeAnnouncementWire);
        using var input = new MemoryStream(wire);

        // Act
        var message = await _serializer.DeserializeMessageAsync(input);
        using var output = new MemoryStream();
        await _serializer.SerializeAsync(message!, output);

        // Assert
        var announcement = Assert.IsType<NodeAnnouncementMessage>(message);
        Assert.Equal(MessageTypes.NodeAnnouncement, announcement.Type);
        Assert.Equal(input.Length, input.Position);
        Assert.Equal(wire, output.ToArray());
        Assert.Equal("LN-Liquidity", announcement.Payload.GetAliasText());
        Assert.Equal(LiquidityAdsEclairVectors.NodeId, Convert.ToHexStringLower(announcement.Payload.NodeId));
    }

    [Fact]
    public async Task Given_EclairsNodeAnnouncement_When_Verified_Then_TheSignatureCoversTheRates()
    {
        // Arrange
        var announcement = await ParseAsync();
        var payload = announcement.Payload;

        // Act
        var valid = SecpECDSASignature.TryCreateFromCompact(payload.Signature.Value, out var signature)
                 && ECPubKey.TryCreate((byte[])payload.NodeId, Context.Instance, out _, out var nodeKey)
                 && nodeKey.SigVerify(signature, (byte[])payload.GetSignatureHash());

        // Assert: the extra data is the TLV stream and is signed
        Assert.True(valid);
        Assert.Equal("fd053b2d" + LiquidityAdsEclairVectors.NodeAnnouncementRates,
                     Convert.ToHexStringLower(payload.ExtraData.Span));
        Assert.EndsWith(Convert.ToHexStringLower(payload.ExtraData.Span),
                        Convert.ToHexStringLower(payload.GetSignedData()));
    }

    [Fact]
    public async Task Given_EclairsNodeAnnouncement_When_TheRatesAreRead_Then_TheyAreEclairsTwoRates()
    {
        // Arrange
        var announcement = await ParseAsync();

        // Act
        var fromExtraData = NodeAnnouncementRates.TryRead(announcement.Payload.ExtraData.Span, out var rates);
        var fromRaw = NodeAnnouncementRates.TryReadFromAnnouncement(announcement.Payload.GetBytes(),
                                                                    out var rawRates);

        // Assert
        Assert.True(fromExtraData);
        Assert.True(fromRaw);
        Assert.NotNull(rates);
        Assert.Equal(rates, rawRates);
        Assert.Equal([
                         new FundingRate(100_000, 500_000, 550, 100, 5_000, 1_000),
                         new FundingRate(500_000, 5_000_000, 1_100, 75, 0, 1_500)
                     ], rates.Rates);
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalance));
        Assert.Equal(new FundingRate(500_000, 5_000_000, 1_100, 75, 0, 1_500), rates.FindRate(750_000));
    }

    private async Task<NodeAnnouncementMessage> ParseAsync()
    {
        using var input = new MemoryStream(Convert.FromHexString(LiquidityAdsEclairVectors.NodeAnnouncementWire));
        return Assert.IsType<NodeAnnouncementMessage>(await _serializer.DeserializeMessageAsync(input));
    }
}