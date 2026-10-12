namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.ValueObjects;

/// <summary>
/// Next Closee Nonce TLV.
/// </summary>
/// <remarks>
/// Simple taproot channels <c>next_closee_nonce</c>, type 22 of <c>closing_sig</c>: [<c>66*byte</c>:<c>public_nonce</c>],
/// the closee's nonce for the next <c>closing_complete</c> (an RBF of the close). LND 0.21 requires it. Converted by
/// <c>NextCloseeNonceTlvConverter</c>.
/// </remarks>
public sealed class NextCloseeNonceTlv(MusigPublicNonce nonce)
    : PublicNonceTlv(TaprootTlvConstants.NextCloseeNonce, nonce);