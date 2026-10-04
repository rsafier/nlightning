using System.Runtime.Serialization;
using NLightning.Domain.Money;

namespace NLightning.Infrastructure.Serialization.Tests.Tlv;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.OnionMessages;
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
        // Given (BOLT 1: the records are added in ascending type order, as on the wire)
        var tlvStream = new TlvStream();
        var tlv1 = new FundingOutputContributionTlv(LightningMoney.Satoshis(100_000));
        var tlv2 = new RequireConfirmedInputsTlv();
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
    public async Task Given_TypesNotStrictlyIncreasing_When_Serialized_Then_ThrowsSerializationException()
    {
        // Given: a hand-built stream whose records descend (type 1 after type 2); BOLT 1 requires ascending types on
        // the wire, so writing must fail instead of silently re-sorting (NL-013)
        var tlvStream = new TlvStream();
        tlvStream.Add(new BaseTlv(new BigSize(2), [0x02]));
        tlvStream.Add(new BaseTlv(new BigSize(1), [0x01]));
        using var memoryStream = new MemoryStream();

        // When & Then
        await Assert.ThrowsAsync<SerializationException>(() =>
                                                             _tlvStreamSerializer.SerializeAsync(tlvStream,
                                                                 memoryStream));
    }

    [Fact]
    public async Task Given_TlvsInInsertionOrder_When_Serialized_Then_WireOrderMatchesInsertionOrder()
    {
        // Given: ascending records keep their insertion order on the wire (no silent re-sort) (NL-013)
        var tlvStream = new TlvStream();
        tlvStream.Add(new BaseTlv(new BigSize(1), [0x01]));
        tlvStream.Add(new BaseTlv(new BigSize(2), [0x02]));
        using var memoryStream = new MemoryStream();

        // When
        await _tlvStreamSerializer.SerializeAsync(tlvStream, memoryStream);

        // Then
        Assert.Equal([0x01, 0x01, 0x01, 0x02, 0x01, 0x02], memoryStream.ToArray());
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

    private static readonly FundingRate s_liquidityRate = new(25_000, 250_000, 750, 150, 50, 500);

    private static Dictionary<Type, BaseTlv> CreateSampleTlvs()
    {
        var pubKey = Convert.FromHexString("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb");
        BaseTlv[] samples =
        [
            new AttributionDataTlv(Enumerable.Range(0, AttributionDataTlv.ValueLength).Select(i => (byte)i).ToArray()),
            new FulfillmentPayloadTlv([0xca, 0xfe]),
            new BlindedPathTlv(new CompactPubKey(pubKey)),
            new ChannelTypeTlv([0x10, 0x00]),
            new FeeRangeTlv(LightningMoney.Satoshis(1_000), LightningMoney.Satoshis(2_000)),
            new FundingOutputContributionTlv(-100_000L),
            new FundingTxIdTlv(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray()),
            new NetworksTlv([BitcoinNetwork.Mainnet.ChainHash]),
            new MyCurrentFundingLockedTlv(new TxId(Enumerable.Range(0, 32).Select(i => (byte)(i + 4)).ToArray()), 1),
            new NextFundingTlv(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            new RemoteAddressTlv(1, "192.168.0.1", 9735),
            new RequireConfirmedInputsTlv(),
            new RequestFundingTlv(new RequestFunding(50_000, s_liquidityRate, LiquidityPaymentDetails.FromChannelBalance)),
            new ProvideFundingTlv(new WillFund(s_liquidityRate, [0xde, 0xad, 0xbe, 0xef], new byte[64])),
            new WillFundRatesTlv(WillFundRates.Create([s_liquidityRate], [LiquidityPaymentType.FromChannelBalance])),
            new SharedInputSignatureTlv(Enumerable.Range(0, SharedInputSignatureTlv.ValueLength).Select(i => (byte)(i + 2))
                                                  .ToArray()),
            new SharedInputTxIdTlv(Enumerable.Range(0, 32).Select(i => (byte)(i + 3)).ToArray()),
            new ShortChannelIdTlv(new ShortChannelId(1234, 0, 1)),
            StartBatchMessageTypeTlv.CommitmentSigned(),
            new UpfrontShutdownScriptTlv(new BitcoinScript([0x00, 0x14, .. new byte[20]])),
            new CommitNoncesTlv(Nonce(0x21), Nonce(0x22)),
            new CurrentCommitNonceTlv(Nonce(0x23)),
            new FundingNonceTlv(Nonce(0x24)),
            new NextCloseeNonceTlv(Nonce(0x25)),
            new NextLocalNonceTlv(Nonce(0x26)),
            new NextLocalNoncesTlv(new FundingNonces([(new TxId(Enumerable.Repeat((byte)0x27, 32).ToArray()),
                                                        Nonce(0x28))])),
            new PartialSignatureWithNonceTlv(new MusigPartialSignatureWithNonce(
                                                 Enumerable.Repeat((byte)0x29, 98).ToArray())),
            new PrevTxDetailsTlv(new TxId(Enumerable.Repeat((byte)0x2c, 32).ToArray()), 100_000,
                                 new BitcoinScript([0x51, 0x20, .. Enumerable.Repeat((byte)0x2d, 32)])),
            new SharedInputPartialSignatureTlv(new MusigPartialSignatureWithNonce(
                                                   Enumerable.Repeat((byte)0x2a, 98).ToArray())),
            new ShutdownNonceTlv(Nonce(0x2b)),
            new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000_000)),
            new OutgoingCltvValueTlv(800_000),
            new OnionShortChannelIdTlv(new ShortChannelId(800_000, 1, 2)),
            new PaymentDataTlv(new Secret(Enumerable.Repeat((byte)0x11, 32).ToArray()),
                               LightningMoney.MilliSatoshis(5_000_000)),
            new EncryptedRecipientDataTlv([0xde, 0xad, 0xbe, 0xef]),
            new CurrentPathKeyTlv(new CompactPubKey(pubKey)),
            new PaymentMetadataTlv([0x01, 0x02, 0x03]),
            new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(10_000_000)),
            new OutgoingNodeIdTlv(new CompactPubKey(pubKey)),
            new TrampolineOnionPacketTlv([0x00, .. pubKey, .. new byte[64]]),
            new RecipientFeaturesTlv([0x02, 0x00, 0x00]),
            new RecipientBlindedPathsTlv([
                new WireBlindedPaymentPath(
                    new WireBlindedPath(SciddirOrPubkey.FromNodeId(new CompactPubKey(pubKey)), new CompactPubKey(pubKey),
                                        [new BlindedPathHop(new CompactPubKey(pubKey), new byte[] { 0x01, 0x02 })]),
                    new BlindedPayInfo(1_000, 100, 40, 1, 1_000_000))
            ])
        ];

        return samples.ToDictionary(t => t.GetType());
    }

    private static MusigPublicNonce Nonce(byte fill) => new(Enumerable.Repeat(fill, 66).ToArray());

    private sealed class UnregisteredTlv() : BaseTlv(new BigSize(99), [0x01]);
}