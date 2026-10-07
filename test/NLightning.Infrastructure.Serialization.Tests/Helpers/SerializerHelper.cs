namespace NLightning.Infrastructure.Serialization.Tests.Helpers;

using Factories;
using NLightning.Infrastructure.Serialization.Wire;
using Serialization.Tlv;

public static class SerializerHelper
{
    public static readonly ValueObjectSerializerFactory ValueObjectSerializerFactory;
    public static readonly WireRegistry WireRegistry;
    public static readonly TlvStreamSerializer TlvStreamSerializer;
    public static readonly TlvSerializer TlvSerializer;

    static SerializerHelper()
    {
        ValueObjectSerializerFactory = new ValueObjectSerializerFactory();
        WireRegistry = new WireRegistry();
        TlvStreamSerializer =
            new TlvStreamSerializer(WireRegistry, new TlvSerializer(ValueObjectSerializerFactory));
        TlvSerializer = new TlvSerializer(ValueObjectSerializerFactory);
    }
}