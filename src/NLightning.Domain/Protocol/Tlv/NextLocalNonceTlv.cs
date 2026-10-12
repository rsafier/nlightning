namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.ValueObjects;

/// <summary>
/// Next Local Nonce TLV.
/// </summary>
/// <remarks>
/// Simple taproot channels <c>next_local_nonce</c>, type 4 of <c>open_channel</c>, <c>accept_channel</c> and
/// <c>channel_ready</c>: [<c>66*byte</c>:<c>public_nonce</c>], the sender's verification nonce for the next commitment
/// the peer signs for it. Converted by <c>NextLocalNonceTlvConverter</c>.
/// </remarks>
public sealed class NextLocalNonceTlv(MusigPublicNonce nonce) : PublicNonceTlv(TaprootTlvConstants.NextLocalNonce, nonce);