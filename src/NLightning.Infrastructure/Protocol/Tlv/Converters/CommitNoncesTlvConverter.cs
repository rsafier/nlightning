using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>commit_nonces</c> (type 4 of tx_complete, BOLTs PR #1324): the current then the next commitment nonce.
/// </summary>
public class CommitNoncesTlvConverter : ITlvConverter<CommitNoncesTlv>
{
    public BaseTlv ConvertToBase(CommitNoncesTlv tlv)
    {
        return tlv;
    }

    public CommitNoncesTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TaprootTlvConstants.CommitNonces)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != CommitNoncesTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
            throw new InvalidCastException(
                $"Invalid length: commit_nonces holds {CommitNoncesTlv.ValueLength} bytes, not {baseTlv.Value.Length}");

        return new CommitNoncesTlv(new MusigPublicNonce(baseTlv.Value[..MusigConstants.PublicNonceLen]),
                                   new MusigPublicNonce(baseTlv.Value[MusigConstants.PublicNonceLen..]));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as CommitNoncesTlv
                          ?? throw new InvalidCastException($"Error converting BaseTlv to {nameof(CommitNoncesTlv)}"));
    }
}