namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.Constants;
using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// Partial Signature With Nonce TLV.
/// </summary>
/// <remarks>
/// Simple taproot channels <c>partial_signature_with_nonce</c>, type 2 of <c>funding_created</c>,
/// <c>funding_signed</c> and <c>commitment_signed</c>: [<c>32*byte</c>:<c>partial_signature</c>]
/// [<c>66*byte</c>:<c>public_nonce</c>], the sender's MuSig2 partial signature of the peer's commitment and the signing
/// nonce it used (the message's 64-byte <c>signature</c> field is then all zeros,
/// <see cref="CompactSignature.Zero"/>). Converted by <c>PartialSignatureWithNonceTlvConverter</c>.
/// </remarks>
public class PartialSignatureWithNonceTlv : BaseTlv
{
    /// <summary>The size of the TLV value: a 32-byte partial signature and a 66-byte public nonce.</summary>
    public const int ValueLength = MusigConstants.PartialSignatureWithNonceLen;

    /// <summary>The partial signature and the nonce.</summary>
    public MusigPartialSignatureWithNonce PartialSignatureWithNonce { get; }

    public PartialSignatureWithNonceTlv(MusigPartialSignatureWithNonce partialSignatureWithNonce)
        : this(TaprootTlvConstants.PartialSignatureWithNonce, partialSignatureWithNonce)
    {
    }

    protected PartialSignatureWithNonceTlv(BigSize type, MusigPartialSignatureWithNonce partialSignatureWithNonce)
        : base(type)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if ((byte[])partialSignatureWithNonce.PartialSignature is null)
            throw new ArgumentException("The partial signature with nonce is empty.",
                                        nameof(partialSignatureWithNonce));

        PartialSignatureWithNonce = partialSignatureWithNonce;

        Value = partialSignatureWithNonce.ToBytes();
        Length = Value.Length;
    }
}