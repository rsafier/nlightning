using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Factories;

using Domain.Protocol.Constants;
using Interfaces;

public class MessageTypeSerializerFactory : IMessageTypeSerializerFactory
{
    private readonly Dictionary<Type, IMessageTypeSerializer> _serializers = new();
    private readonly Dictionary<MessageTypes, Type> _messageTypeDictionary = new();
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;
    private readonly Wire.WireRegistry _wireRegistry;

    public MessageTypeSerializerFactory(IPayloadSerializerFactory payloadSerializerFactory,
                                        ITlvConverterFactory tlvConverterFactory,
                                        ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
        _wireRegistry = new Wire.WireRegistry(tlvConverterFactory);
    }

    public IMessageTypeSerializer<TMessageType>? GetSerializer<TMessageType>() where TMessageType : IMessage
    {
        // Migrated messages are wire definitions; the hand-written serializers serve the rest
        if (_wireRegistry.Get<TMessageType>() is { } wire)
            return wire as IMessageTypeSerializer<TMessageType>;

        return _serializers.GetValueOrDefault(typeof(TMessageType)) as IMessageTypeSerializer<TMessageType>;
    }

    public IMessageTypeSerializer? GetSerializer(MessageTypes messageType)
    {
        if (_wireRegistry.Get(messageType) is { } wire)
            return wire;

        var type = _messageTypeDictionary.GetValueOrDefault(messageType);
        if (type is null)
            return null;

        return _serializers.GetValueOrDefault(type);
    }
}