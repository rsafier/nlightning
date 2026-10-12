using System.Runtime.Serialization;

namespace NLightning.Infrastructure.Serialization.Wire;

using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>A record's wire type and its value codec, declared alongside its message.</summary>
public class TlvDef
{
    private readonly Func<BaseTlv, object?> _decode;
    private readonly Func<BaseTlv, BaseTlv>? _encode;

    protected TlvDef(BigSize type, Type? runtimeType, Func<BaseTlv, object?> decode,
                     Func<BaseTlv, BaseTlv>? encode = null)
    {
        Type = type;
        RuntimeType = runtimeType;
        _decode = decode;
        _encode = encode;
        ValueDefinition = this;
    }

    public BigSize Type { get; }
    public Type? RuntimeType { get; }
    internal TlvDef ValueDefinition { get; private init; }

    public bool IsLenient { get; private init; }

    public static TlvDef<TTlv> Typed<TTlv>(BigSize type, Func<BaseTlv, TTlv> decode,
                                          Func<TTlv, BaseTlv> encode) where TTlv : BaseTlv
        => new(type, decode, encode);

    /// <summary>A known raw record with the exact length used by closing signatures.</summary>
    public static TlvDef Raw(BigSize type, int exactLength) => new(type, null, raw =>
    {
        if (raw.Value.Length != exactLength)
            throw new SerializationException(
                $"TLV type {type.Value} holds {raw.Value.Length} bytes, not {exactLength}");
        return raw.Value;
    });

    /// <summary>A known raw gossip-query record, retained as received.</summary>
    public static TlvDef RawKnown(BigSize type) => new(type, null, raw => raw.Value);

    /// <summary>Advisory values may fail to decode; the message can still retain their raw bytes.</summary>
    public TlvDef AsLenient() => new(Type, RuntimeType, raw =>
    {
        try
        {
            return _decode(raw);
        }
        catch (Exception e) when (e is InvalidCastException or ArgumentException)
        {
            return null;
        }
    }, _encode)
    { IsLenient = true, ValueDefinition = ValueDefinition };

    /// <summary>The same value codec under an alternate wire tag (Eclair's prevtx_details).</summary>
    public TlvDef WithType(BigSize type) => new(type, RuntimeType, _decode, _encode) { ValueDefinition = ValueDefinition };

    public object? Decode(BaseTlv raw) => _decode(raw);

    public BaseTlv Encode(BaseTlv tlv) => _encode is not null
        ? _encode(tlv)
        : tlv;

    internal object? DecodeStrict(BaseTlv raw)
    {
        try
        {
            return _decode(raw);
        }
        catch (Exception e) when (!IsLenient && e is not SerializationException)
        {
            throw new SerializationException($"Error deserializing TLV type {Type.Value}", e);
        }
    }
}

/// <summary>The typed view of a TLV value definition.</summary>
public sealed class TlvDef<TTlv> : TlvDef where TTlv : BaseTlv
{
    internal TlvDef(BigSize type, Func<BaseTlv, TTlv> decode, Func<TTlv, BaseTlv> encode)
        : base(type, typeof(TTlv), raw => decode(raw), raw => encode(raw as TTlv
            ?? throw new InvalidCastException($"Error converting BaseTlv to {typeof(TTlv).Name}")))
    {
    }

    public new TTlv Decode(BaseTlv raw) => (TTlv)base.Decode(raw)!;
}