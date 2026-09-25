namespace NLightning.Domain.Protocol.Interfaces;

using Tlv;

public interface ITlvConverterFactory
{
    ITlvConverter<TTlv>? GetConverter<TTlv>() where TTlv : BaseTlv;

    /// <summary>
    /// Gets the converter registered for the exact runtime type <paramref name="tlvType"/>.
    /// </summary>
    /// <param name="tlvType">The concrete <see cref="BaseTlv"/> subtype.</param>
    /// <returns>The converter, or <c>null</c> when no converter is registered for that type.</returns>
    ITlvConverter? GetConverter(Type tlvType);
}