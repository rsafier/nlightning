using NLightning.Domain.Protocol.Tlv;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Infrastructure.Serialization.Factories;
using NLightning.Infrastructure.Serialization.Tlv;

namespace NLightning.Infrastructure.Serialization.Tests.Tlv;

public class TlvSerializerTests
{
    private readonly TlvSerializer _tlvSerializer;

    public TlvSerializerTests()
    {
        _tlvSerializer = new TlvSerializer(new ValueObjectSerializerFactory());
    }

    [Fact]
    public async Task Given_TlvSerializer_When_SerializingBaseTlv_Then_BufferIsCorrect()
    {
        // Given
        var baseTlv = new BaseTlv(0, 3, [0x01, 0x02, 0x03]);
        var expectedBuffer = new byte[] { 0x00, 0x03, 0x01, 0x02, 0x03 };
        using var stream = new MemoryStream();

        // When
        await _tlvSerializer.SerializeAsync(baseTlv, stream);
        stream.Position = 0;
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, 0, (int)stream.Length, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(expectedBuffer, buffer);
    }

    [Fact]
    public async Task Given_TlvSerializer_When_DeserializingBaseTlv_Then_BufferIsCorrect()
    {
        // Given
        var expectedType = new BigSize(0);
        var expectedLength = new BigSize(3);
        byte[] expectedValue = [0x01, 0x02, 0x03];
        using var stream = new MemoryStream([0x00, 0x03, 0x01, 0x02, 0x03]);

        // When
        var baseTlv = await _tlvSerializer.DeserializeAsync(stream);

        // Then
        Assert.NotNull(baseTlv);
        Assert.Equal(expectedType, baseTlv.Type);
        Assert.Equal(expectedLength, baseTlv.Length);
        Assert.Equal(expectedValue, baseTlv.Value);
    }
}