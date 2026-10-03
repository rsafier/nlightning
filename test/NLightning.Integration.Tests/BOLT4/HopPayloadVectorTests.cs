using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Protocol.Onion.Validators;
using Infrastructure.Protocol.Factories;
using Infrastructure.Serialization.Factories;
using Infrastructure.Serialization.Onion;
using Infrastructure.Serialization.Tlv;

public class HopPayloadVectorTests
{
    private readonly HopPayloadSerializer _serializer;

    public HopPayloadVectorTests()
    {
        var valueObjectSerializerFactory = new ValueObjectSerializerFactory();
        var tlvConverterFactory = new TlvConverterFactory();
        var tlvSerializer = new TlvSerializer(valueObjectSerializerFactory);
        var tlvStreamSerializer = new TlvStreamSerializer(tlvConverterFactory, tlvSerializer);

        _serializer = new HopPayloadSerializer(tlvSerializer, tlvStreamSerializer, tlvConverterFactory,
                                               valueObjectSerializerFactory);
    }

    [Fact]
    public async Task Given_OnionTestPayloads_When_RoundTripping_Then_EveryPayloadIsByteIdentical()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();

        foreach (var hop in vector.Hops)
        {
            using var input = new MemoryStream(hop.Payload);

            // Act
            var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);
            using var output = new MemoryStream();
            await _serializer.SerializeWithLengthPrefixAsync(payload, output);

            // Assert
            Assert.Equal(hop.Payload.Length, input.Position);
            Assert.Equal(hop.Payload, output.ToArray());
        }
    }

    [Fact]
    public async Task Given_OnionTestPayloads_When_Deserializing_Then_UnknownOddTlvsArePreserved()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var unknownTypesByHop = new List<ulong[]>();

        // Act
        foreach (var hop in vector.Hops)
        {
            using var input = new MemoryStream(hop.Payload);
            var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);
            unknownTypesByHop.Add(payload.UnknownTlvs.Select(tlv => tlv.Type.Value).ToArray());
        }

        // Assert
        Assert.Empty(unknownTypesByHop[0]);
        Assert.Equal([513UL], unknownTypesByHop[1]);
        Assert.Empty(unknownTypesByHop[2]);
        Assert.Empty(unknownTypesByHop[3]);
        Assert.Equal([301UL], unknownTypesByHop[4]);
    }

    [Fact]
    public async Task Given_OnionTestPayloads_When_Validating_Then_IntermediateAndFinalRulesPass()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var lastHop = vector.Hops.Count - 1;

        for (var i = 0; i < vector.Hops.Count; i++)
        {
            using var input = new MemoryStream(vector.Hops[i].Payload);
            var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);

            // Act
            var isValid = HopPayloadValidator.TryValidate(payload, i == lastHop, false, out var error);

            // Assert
            Assert.True(isValid, error?.Message);
        }
    }
}