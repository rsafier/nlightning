namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.ValueObjects;

/// <summary>
/// Shutdown Nonce TLV.
/// </summary>
/// <remarks>
/// Simple taproot channels <c>shutdown_nonce</c>, type 8 of <c>shutdown</c>: [<c>66*byte</c>:<c>public_nonce</c>], the
/// sender's closee nonce (the one it signs its first <c>closing_sig</c> with). Converted by
/// <c>ShutdownNonceTlvConverter</c>.
/// </remarks>
public sealed class ShutdownNonceTlv(MusigPublicNonce nonce) : PublicNonceTlv(TaprootTlvConstants.ShutdownNonce, nonce);