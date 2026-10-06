namespace NLightning.Infrastructure.Serialization.Tests.Helpers;

using Factories;
using NLightning.Infrastructure.Serialization.Wire;
using Serialization.Node;
using Serialization.Tlv;

public static class SerializerHelper
{
    public static readonly ValueObjectSerializerFactory ValueObjectSerializerFactory;
    public static readonly PayloadSerializerFactory PayloadSerializerFactory;
    public static readonly WireRegistry WireRegistry;
    public static readonly TlvStreamSerializer TlvStreamSerializer;
    public static readonly TlvSerializer TlvSerializer;
    public static readonly MessageTypeSerializerFactory MessageTypeSerializerFactory;

    static SerializerHelper()
    {
        ValueObjectSerializerFactory = new ValueObjectSerializerFactory();
        PayloadSerializerFactory =
            new PayloadSerializerFactory(new FeatureSetSerializer(), ValueObjectSerializerFactory);
        WireRegistry = new WireRegistry();
        TlvStreamSerializer =
            new TlvStreamSerializer(WireRegistry, new TlvSerializer(ValueObjectSerializerFactory));
        TlvSerializer = new TlvSerializer(ValueObjectSerializerFactory);
        MessageTypeSerializerFactory =
            new MessageTypeSerializerFactory(PayloadSerializerFactory, TlvStreamSerializer);
    }
}