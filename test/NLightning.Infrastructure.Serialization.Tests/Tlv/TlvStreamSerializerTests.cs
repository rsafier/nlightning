using System.Runtime.Serialization;
using NLightning.Domain.Money;

namespace NLightning.Infrastructure.Serialization.Tests.Tlv;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Helpers;
using Serialization.Tlv;

public class TlvStreamSerializerTests
{
    private readonly TlvStreamSerializer _tlvStreamSerializer;

    public TlvStreamSerializerTests()
    {
        _tlvStreamSerializer = new TlvStreamSerializer(SerializerHelper.TlvConverterFactory,
                                                       SerializerHelper.TlvSerializer);
    }

    [Fact]
    public async Task Given_TlvStream_When_SerializedAndDeserialized_Then_DataIsPreserved()
    {
        // Given
        var tlvStream = new TlvStream();
        var tlv1 = new RequireConfirmedInputsTlv();
        var tlv2 = new FundingOutputContributionTlv(LightningMoney.Satoshis(100_000));
        tlvStream.Add(tlv1, tlv2);

        using var memoryStream = new MemoryStream();

        // When
        await _tlvStreamSerializer.SerializeAsync(tlvStream, memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);
        var deserializedTlvStream = await _tlvStreamSerializer.DeserializeAsync(memoryStream);

        // Then
        Assert.NotNull(deserializedTlvStream);
        Assert.True(deserializedTlvStream.TryGetTlv(tlv1.Type, out var retrievedTlv1));
        Assert.NotNull(retrievedTlv1);
        Assert.Equal(tlv1.Type, retrievedTlv1.Type);
        Assert.True(deserializedTlvStream.TryGetTlv(tlv2.Type, out var retrievedTlv2));
        Assert.NotNull(retrievedTlv2);
        Assert.Equal(tlv2.Type, retrievedTlv2.Type);
    }

    [Fact]
    public async Task Given_EmptyStream_When_Deserialized_Then_ReturnsNull()
    {
        // Given
        using var memoryStream = new MemoryStream();

        // When
        var deserializedTlvStream = await _tlvStreamSerializer.DeserializeAsync(memoryStream);

        // Then
        Assert.Null(deserializedTlvStream);
    }

    [Fact]
    public async Task Given_InvalidStream_When_Deserialized_Then_ThrowsSerializationException()
    {
        // Given
        using var memoryStream = new MemoryStream([0x00]);

        // When & Then
        await Assert.ThrowsAsync<SerializationException>(async () => await _tlvStreamSerializer.DeserializeAsync(memoryStream));
    }

    [Fact]
    public async Task Given_EveryRegisteredConverter_When_SerializedThroughTlvStream_Then_BytesMatchConverterOutput()
    {
        // Arrange
        var registeredTypes = SerializerHelper.TlvConverterFactory.RegisteredTlvTypes;
        var samples = CreateSampleTlvs();
        var missing = registeredTypes.Where(t => !samples.ContainsKey(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0,
                    $"Add a sample to {nameof(CreateSampleTlvs)} for: {string.Join(", ", missing)}");

        foreach (var type in registeredTypes)
        {
            var sample = samples[type];
            var converter = SerializerHelper.TlvConverterFactory.GetConverter(type);
            Assert.NotNull(converter);
            var expectedBase = converter.ConvertToBase(sample);
            using var expectedStream = new MemoryStream();
            await SerializerHelper.TlvSerializer.SerializeAsync(expectedBase, expectedStream);

            var tlvStream = new TlvStream();
            tlvStream.Add(sample);
            using var memoryStream = new MemoryStream();

            // Act
            await _tlvStreamSerializer.SerializeAsync(tlvStream, memoryStream);

            // Assert
            Assert.Equal(expectedStream.ToArray(), memoryStream.ToArray());
            memoryStream.Position = 0;
            var deserialized = await _tlvStreamSerializer.DeserializeAsync(memoryStream);
            Assert.NotNull(deserialized);
            Assert.True(deserialized.TryGetTlv(sample.Type, out var rawTlv));
            Assert.NotNull(rawTlv);
            Assert.Equal(expectedBase.Value, rawTlv.Value);
            var roundTripped = converter.ConvertFromBase(rawTlv);
            Assert.IsType(type, roundTripped);
        }
    }

    [Fact]
    public async Task Given_RawBaseTlv_When_Serialized_Then_WrittenVerbatim()
    {
        // Arrange
        var tlvStream = new TlvStream();
        tlvStream.Add(new BaseTlv(new BigSize(0xfd00ff), [0x2a, 0x2b]));
        using var memoryStream = new MemoryStream();

        // Act
        await _tlvStreamSerializer.SerializeAsync(tlvStream, memoryStream);

        // Assert
        Assert.Equal(new byte[] { 0xfe, 0x00, 0xfd, 0x00, 0xff, 0x02, 0x2a, 0x2b }, memoryStream.ToArray());
    }

    [Fact]
    public async Task Given_UnregisteredTlvSubtype_When_Serialized_Then_ThrowsSerializationException()
    {
        // Arrange
        var tlvStream = new TlvStream();
        tlvStream.Add(new UnregisteredTlv());
        using var memoryStream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => _tlvStreamSerializer.SerializeAsync(tlvStream,
                                                             memoryStream));
    }

    [Fact]
    public async Task Given_TypesNotStrictlyIncreasing_When_Deserialized_Then_ThrowsSerializationException()
    {
        // Given
        using var memoryStream = new MemoryStream([0x03, 0x00, 0x01, 0x00]);

        // When & Then
        await Assert.ThrowsAsync<SerializationException>(() => _tlvStreamSerializer.DeserializeAsync(memoryStream));
    }

    [Fact]
    public async Task Given_LengthExceedsRemainingBytes_When_Deserialized_Then_ThrowsSerializationException()
    {
        // Given: length 0xffffffff with only one value byte
        using var memoryStream = new MemoryStream([0x01, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00]);

        // When & Then
        await Assert.ThrowsAsync<SerializationException>(() => _tlvStreamSerializer.DeserializeAsync(memoryStream));
    }

    [Fact]
    public async Task Given_UnknownEvenType_When_DeserializedStrict_Then_ThrowsSerializationException()
    {
        // Given
        using var memoryStream = new MemoryStream([0x01, 0x00, 0x04, 0x00]);
        var knownTypes = new HashSet<BigSize> { 1UL, 2UL };

        // When & Then
        await Assert.ThrowsAsync<SerializationException>(() => _tlvStreamSerializer
                                                            .DeserializeStrictAsync(memoryStream, knownTypes));
    }

    [Fact]
    public async Task Given_EmptyStream_When_DeserializedStrict_Then_ReturnsEmptyTlvStream()
    {
        // Given
        using var memoryStream = new MemoryStream();

        // When
        var tlvStream = await _tlvStreamSerializer.DeserializeStrictAsync(memoryStream, new HashSet<BigSize>());

        // Then
        Assert.False(tlvStream.Any());
    }

    private static Dictionary<Type, BaseTlv> CreateSampleTlvs()
    {
        var pubKey = Convert.FromHexString("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb");
        BaseTlv[] samples =
        [
            new BlindedPathTlv(new CompactPubKey(pubKey)),
            new ChannelTypeTlv([0x10, 0x00]),
            new FeeRangeTlv(LightningMoney.Satoshis(1_000), LightningMoney.Satoshis(2_000)),
            new FundingOutputContributionTlv(LightningMoney.Satoshis(100_000)),
            new NetworksTlv([BitcoinNetwork.Mainnet.ChainHash]),
            new NextFundingTlv(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            new RemoteAddressTlv(1, "192.168.0.1", 9735),
            new RequireConfirmedInputsTlv(),
            new ShortChannelIdTlv(new ShortChannelId(1234, 0, 1)),
            new UpfrontShutdownScriptTlv(new BitcoinScript([0x00, 0x14, .. new byte[20]]))
        ];

        return samples.ToDictionary(t => t.GetType());
    }

    private sealed class UnregisteredTlv() : BaseTlv(new BigSize(99), [0x01]);
}