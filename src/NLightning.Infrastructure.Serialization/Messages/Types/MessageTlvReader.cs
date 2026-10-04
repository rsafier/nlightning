using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Reads one typed TLV out of a message's extension through its registered converter.
/// </summary>
internal static class MessageTlvReader
{
    /// <summary>
    /// The TLV of <paramref name="type"/> converted to <typeparamref name="TTlv"/>, or null when the extension does not
    /// hold it.
    /// </summary>
    /// <exception cref="SerializationException">No converter is registered for <typeparamref name="TTlv"/>.</exception>
    /// <exception cref="InvalidCastException">The converter refused the value (a malformed TLV).</exception>
    public static TTlv? ReadTlv<TTlv>(this TlvStream? extension, BigSize type, ITlvConverterFactory tlvConverterFactory)
        where TTlv : BaseTlv
    {
        if (extension is null || !extension.TryGetTlv(type, out var baseTlv) || baseTlv is null)
            return null;

        var converter = tlvConverterFactory.GetConverter<TTlv>()
                     ?? throw new SerializationException($"No serializer found for tlv type {typeof(TTlv).Name}");
        return converter.ConvertFromBase(baseTlv);
    }
}