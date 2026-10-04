using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Converts a 98-byte partial signature with nonce TLV (<see cref="PartialSignatureWithNonceTlv"/> and its
/// subclasses): <c>s || R1 || R2</c>.
/// </summary>
public abstract class PartialSignatureWithNonceTlvConverterBase<TTlv> : ITlvConverter<TTlv>
    where TTlv : PartialSignatureWithNonceTlv
{
    /// <summary>The TLV type this converter reads.</summary>
    protected abstract BigSize TlvType { get; }

    /// <summary>Builds the TLV from its value.</summary>
    protected abstract TTlv Create(MusigPartialSignatureWithNonce partialSignatureWithNonce);

    public BaseTlv ConvertToBase(TTlv tlv)
    {
        return tlv;
    }

    public TTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvType)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != PartialSignatureWithNonceTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
            throw new InvalidCastException(
                $"Invalid length: a partial signature with nonce holds {PartialSignatureWithNonceTlv.ValueLength} "
              + $"bytes, not {baseTlv.Value.Length}");

        return Create(new MusigPartialSignatureWithNonce(baseTlv.Value));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as TTlv
                          ?? throw new InvalidCastException($"Error converting BaseTlv to {typeof(TTlv).Name}"));
    }
}

/// <summary>
/// Converts <c>partial_signature_with_nonce</c> (type 2 of funding_created, funding_signed and commitment_signed).
/// </summary>
public sealed class PartialSignatureWithNonceTlvConverter
    : PartialSignatureWithNonceTlvConverterBase<PartialSignatureWithNonceTlv>
{
    protected override BigSize TlvType => TaprootTlvConstants.PartialSignatureWithNonce;

    protected override PartialSignatureWithNonceTlv Create(MusigPartialSignatureWithNonce value) => new(value);
}

/// <summary>Converts <c>shared_input_partial_signature</c> (type 2 of tx_signatures, BOLTs PR #1324).</summary>
public sealed class SharedInputPartialSignatureTlvConverter
    : PartialSignatureWithNonceTlvConverterBase<SharedInputPartialSignatureTlv>
{
    protected override BigSize TlvType => TaprootTlvConstants.SharedInputPartialSignature;

    protected override SharedInputPartialSignatureTlv Create(MusigPartialSignatureWithNonce value) => new(value);
}