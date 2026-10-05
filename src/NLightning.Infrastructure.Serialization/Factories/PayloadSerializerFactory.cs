using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Factories;

using Domain.Protocol.Constants;
using Interfaces;

public class PayloadSerializerFactory : IPayloadSerializerFactory
{
    private readonly IFeatureSetSerializer _featureSetSerializer;
    private readonly Dictionary<Type, IPayloadSerializer> _serializers = new();
    private readonly Dictionary<MessageTypes, Type> _messageTypeDictionary = new();
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public PayloadSerializerFactory(IFeatureSetSerializer featureSetSerializer,
                                    IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _featureSetSerializer = featureSetSerializer;
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public IPayloadSerializer<TPayloadType>? GetSerializer<TPayloadType>() where TPayloadType : IMessagePayload
    {
        return _serializers.GetValueOrDefault(typeof(TPayloadType)) as IPayloadSerializer<TPayloadType>;
    }

    public IPayloadSerializer? GetSerializer(MessageTypes messageType)
    {
        var type = _messageTypeDictionary.GetValueOrDefault(messageType);
        return type is null ? null : _serializers.GetValueOrDefault(type);
    }
}