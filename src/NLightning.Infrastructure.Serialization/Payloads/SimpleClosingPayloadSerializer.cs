using System.Buffers.Binary;
using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Bitcoin.Constants;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Exceptions;

/// <summary>
/// The fields shared by <c>closing_complete</c> and <c>closing_sig</c> (BOLT 2 <c>option_simple_close</c>):
/// <c>channel_id</c>, <c>u16</c> + <c>closer_scriptpubkey</c>, <c>u16</c> + <c>closee_scriptpubkey</c>,
/// <c>u64 fee_satoshis</c>, <c>u32 locktime</c>, all big-endian.
/// </summary>
public abstract class SimpleClosingPayloadSerializer<TPayload> : IPayloadSerializer<TPayload>
    where TPayload : SimpleClosingPayload
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    protected SimpleClosingPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not TPayload closingPayload)
            throw new SerializationException($"Payload is not of type {typeof(TPayload).Name}");

        var channelIdSerializer =
            _valueObjectSerializerFactory.GetSerializer<ChannelId>()
         ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
        await channelIdSerializer.SerializeAsync(closingPayload.ChannelId, stream);

        await WriteScriptAsync(closingPayload.CloserScriptPubKey, stream);
        await WriteScriptAsync(closingPayload.CloseeScriptPubKey, stream);

        var numbers = new byte[sizeof(ulong) + sizeof(uint)];
        BinaryPrimitives.WriteUInt64BigEndian(numbers, (ulong)closingPayload.FeeSatoshis.Satoshi);
        BinaryPrimitives.WriteUInt32BigEndian(numbers.AsSpan(sizeof(ulong)), closingPayload.LockTime);
        await stream.WriteAsync(numbers);
    }

    public async Task<TPayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var channelIdSerializer =
                _valueObjectSerializerFactory.GetSerializer<ChannelId>()
             ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
            var channelId = await channelIdSerializer.DeserializeAsync(stream);

            var closerScript = await ReadScriptAsync(stream);
            var closeeScript = await ReadScriptAsync(stream);

            var numbers = new byte[sizeof(ulong) + sizeof(uint)];
            await stream.ReadExactlyAsync(numbers);
            var feeSatoshis = BinaryPrimitives.ReadUInt64BigEndian(numbers);
            var lockTime = BinaryPrimitives.ReadUInt32BigEndian(numbers.AsSpan(sizeof(ulong)));

            return Create(channelId, closerScript, closeeScript, LightningMoney.Satoshis(feeSatoshis), lockTime);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {typeof(TPayload).Name}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }

    /// <summary>Builds the payload from the decoded fields.</summary>
    protected abstract TPayload Create(ChannelId channelId, BitcoinScript closerScript, BitcoinScript closeeScript,
                                       LightningMoney feeSatoshis, uint lockTime);

    private static async Task WriteScriptAsync(BitcoinScript script, Stream stream)
    {
        var length = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)script.Length));
        await stream.WriteAsync(length);
        await stream.WriteAsync((byte[])script);
    }

    private static async Task<BitcoinScript> ReadScriptAsync(Stream stream)
    {
        var lengthBytes = new byte[sizeof(ushort)];
        await stream.ReadExactlyAsync(lengthBytes);
        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        if (length > ScriptConstants.MaxScriptSize)
            throw new SerializationException(
                $"Script length {length} exceeds maximum size {ScriptConstants.MaxScriptSize}");

        var script = new byte[length];
        await stream.ReadExactlyAsync(script);
        return new BitcoinScript(script);
    }
}