namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.ValueObjects;

/// <summary>
/// Shared Input Partial Signature TLV.
/// </summary>
/// <remarks>
/// BOLTs PR #1324 <c>shared_input_partial_signature</c>, type 2 of <c>tx_signatures</c>:
/// [<c>32*byte</c>:<c>partial_signature</c>] [<c>66*byte</c>:<c>public_nonce</c>], the sender's MuSig2 partial
/// signature of the shared taproot input (the previous funding output) of a splice, the taproot counterpart of
/// <see cref="SharedInputSignatureTlv"/> (type 0). Converted by <c>SharedInputPartialSignatureTlvConverter</c>.
/// </remarks>
public sealed class SharedInputPartialSignatureTlv(MusigPartialSignatureWithNonce partialSignatureWithNonce)
    : PartialSignatureWithNonceTlv(TaprootTlvConstants.SharedInputPartialSignature, partialSignatureWithNonce);